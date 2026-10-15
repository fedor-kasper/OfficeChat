using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using OfficeChat.Protocol;
using OfficeChat.Services;

namespace OfficeChat.Client;

/// <summary>
/// Постоянное соединение с сервером (WebSocket): синхронизация журнала событий, запросы с ответами,
/// «кто в сети» и сигналы. Само переподключается, если сервер пропал. События журнала обрабатываются
/// строго по одному и по порядку в потоке, где создано соединение (UI), а подтверждаются серверу,
/// только когда обработаны, — так ничего не теряется при обрыве.
/// </summary>
public sealed class ServerConnection : IAsyncDisposable
{
    private static readonly TimeSpan[] Backoff =
        { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) };
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly ServerApi _api;
    private readonly Func<long> _lastSeq;
    private readonly Func<Frame, Task> _handleEvent;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Frame>> _pending = new();
    private readonly Channel<Frame> _incoming = Channel.CreateUnbounded<Frame>();
    private ClientWebSocket? _socket;
    private Task? _loop;
    private int _rid;
    private long _ackedSeq;
    private long _processedSeq;

    /// <summary>Соединение установлено и журнал догнан.</summary>
    public bool IsConnected { get; private set; }

    /// <summary>Сервер отверг ключ устройства (вышли на другом устройстве, сбросили пароль, заблокировали).</summary>
    public event Action<string>? AuthFailed;

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<Guid, bool>? PresenceChanged;
    public event Action<Frame>? SignalReceived;
    public event Action<DirectoryEntry>? DirectoryChanged;

    /// <param name="lastSeq">С какого события догонять журнал (последнее обработанное).</param>
    /// <param name="handleEvent">Обработка события журнала (в UI-потоке, по одному).</param>
    public ServerConnection(ServerApi api, Func<long> lastSeq, Func<Frame, Task> handleEvent)
    {
        _api = api;
        _lastSeq = lastSeq;
        _handleEvent = handleEvent;
    }

    public void Start()
    {
        _processedSeq = _ackedSeq = _lastSeq();
        _loop ??= Task.Run(() => RunAsync(_stop.Token));
        Post(() => _ = ProcessLoopAsync());
    }

    /// <summary>Запрос к серверу; ответ — ok или error. Если соединения нет — исключение.</summary>
    public async Task<Frame> RequestAsync(string op, object data, CancellationToken ct = default)
    {
        var socket = _socket;
        if (socket is not { State: WebSocketState.Open }) throw new InvalidOperationException("Нет связи с сервером");
        var rid = "r" + Interlocked.Increment(ref _rid);
        var reply = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[rid] = reply;
        try
        {
            await SendAsync(socket, new Frame
            {
                Op = op, Rid = rid, Data = JsonSerializer.SerializeToElement(data, data.GetType(), ServerProtocol.Json),
            }, ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
            timeout.CancelAfter(RequestTimeout);
            await using (timeout.Token.Register(() => reply.TrySetCanceled()))
                return await reply.Task;
        }
        finally
        {
            _pending.TryRemove(rid, out _);
        }
    }

    /// <summary>Запрос, который должен выполниться: ошибка сервера — исключение с его текстом.</summary>
    public async Task OkAsync(string op, object data, CancellationToken ct = default)
    {
        var reply = await RequestAsync(op, data, ct);
        if (reply.Op != Ops.Ok) throw new ServerApiException(System.Net.HttpStatusCode.BadRequest, reply.Error ?? "", reply.Message ?? "Ошибка сервера");
    }

    /// <summary>Сигнал без ответа и без гарантии доставки («печатает…»).</summary>
    public void Signal(Guid? to, Guid? groupId, string type, JsonElement? payload = null)
    {
        if (_socket is not { State: WebSocketState.Open } socket) return;
        _ = SendQuietlyAsync(socket, new Frame
        {
            Op = Ops.Signal,
            Data = JsonSerializer.SerializeToElement(new SignalData(to, groupId, type, payload), ServerProtocol.Json),
        });
    }

    // ---- Соединение ----

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.RemoteCertificateValidationCallback = _api.ValidateCertificate;
                socket.Options.SetRequestHeader("Authorization", "Bearer " + _api.Token);
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                socket.Options.Proxy = null; // сервер в локальной сети — без системного прокси
                using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    var wsUri = new UriBuilder(_api.BaseUri) { Scheme = "wss", Path = "/api/ws" }.Uri;
                    await socket.ConnectAsync(wsUri, connectTimeout.Token);
                }
                _socket = socket;
                attempt = 0;
                var since = await InvokeOnUiAsync(_lastSeq);
                await SendAsync(socket, new Frame { Op = Ops.Sync, Since = since }, ct);
                Log.Info($"Подключились к серверу {_api.Host}:{_api.Port}, догоняем журнал с {since}");
                await ReceiveLoopAsync(socket, ct);
            }
            catch (WebSocketException ex) when (ex.Message.Contains("401") || ex.Message.Contains("403"))
            {
                Log.Warn("Сервер не принял ключ устройства — нужно войти заново", ex);
                Post(() => AuthFailed?.Invoke(ex.Message.Contains("403")
                    ? "Аккаунт заблокирован администратором"
                    : "Вход больше не действует — войдите заново"));
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException
                                           or HttpRequestException or System.Security.Authentication.AuthenticationException)
            {
                if (ct.IsCancellationRequested) break;
                if (attempt == 0) Log.Info($"Нет связи с сервером {_api.Host}:{_api.Port}: {ex.Message}");
            }
            finally
            {
                _socket = null;
                foreach (var pending in _pending.Values) pending.TrySetCanceled();
                if (IsConnected)
                {
                    IsConnected = false;
                    Post(() => Disconnected?.Invoke());
                }
            }

            try
            {
                await Task.Delay(Backoff[Math.Min(attempt++, Backoff.Length - 1)], ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            Frame? frame;
            try
            {
                frame = JsonSerializer.Deserialize<Frame>(message.GetBuffer().AsSpan(0, (int)message.Length), ServerProtocol.Json);
            }
            catch (JsonException)
            {
                continue;
            }
            if (frame == null) continue;

            if (frame.Rid != null && frame.Op is Ops.Ok or Ops.Error && _pending.TryGetValue(frame.Rid, out var reply))
                reply.TrySetResult(frame);
            else
                await _incoming.Writer.WriteAsync(frame, ct);
        }
    }

    /// <summary>Обработка входящих по одному, в UI-потоке.</summary>
    private async Task ProcessLoopAsync()
    {
        await foreach (var frame in _incoming.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(true))
        {
            try
            {
                switch (frame.Op)
                {
                    case Ops.Event:
                        // После переподключения журнал догоняется заново — уже обработанное пропускаем.
                        if (frame.Seq is { } seq && seq <= _processedSeq) break;
                        await _handleEvent(frame);
                        _processedSeq = Math.Max(_processedSeq, frame.Seq ?? 0);
                        // Подтверждаем пачкой: когда очередь разобрана.
                        if (_incoming.Reader.Count == 0) Acknowledge();
                        break;
                    case Ops.Synced:
                        Acknowledge();
                        if (!IsConnected)
                        {
                            IsConnected = true;
                            Connected?.Invoke();
                        }
                        break;
                    case Ops.Presence when frame.AccountId is { } id:
                        PresenceChanged?.Invoke(id, frame.Online == true);
                        break;
                    case Ops.Signal:
                        SignalReceived?.Invoke(frame);
                        break;
                    case Ops.Directory when frame.Account is { } account:
                        DirectoryChanged?.Invoke(account);
                        break;
                }
            }
            catch (Exception ex)
            {
                // Ошибка обработки одного события не должна останавливать синхронизацию.
                Log.Error($"Не удалось обработать «{frame.Op}/{frame.Kind}» от сервера", ex);
                _processedSeq = Math.Max(_processedSeq, frame.Seq ?? 0);
            }
        }
    }

    private void Acknowledge()
    {
        if (_processedSeq <= _ackedSeq || _socket is not { State: WebSocketState.Open } socket) return;
        _ackedSeq = _processedSeq;
        _ = SendQuietlyAsync(socket, new Frame { Op = Ops.Ack, Seq = _processedSeq });
    }

    private async Task SendAsync(ClientWebSocket socket, Frame frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, ServerProtocol.Json);
        await _sendLock.WaitAsync(ct);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendQuietlyAsync(ClientWebSocket socket, Frame frame)
    {
        try
        {
            await SendAsync(socket, frame, _stop.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private void Post(Action action)
    {
        if (_ui == null) action();
        else _ui.Post(_ => action(), null);
    }

    private Task<T> InvokeOnUiAsync<T>(Func<T> func)
    {
        if (_ui == null) return Task.FromResult(func());
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ =>
        {
            try
            {
                result.SetResult(func());
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
        }, null);
        return result.Task;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _incoming.Writer.TryComplete();
        if (_socket is { State: WebSocketState.Open } socket)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
        if (_loop != null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
