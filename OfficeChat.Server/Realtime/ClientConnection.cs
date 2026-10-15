using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OfficeChat.Protocol;
using OfficeChat.Server.Data;

namespace OfficeChat.Server.Realtime;

/// <summary>
/// Одно подключённое устройство (WebSocket). Читает запросы и отвечает на них; события журнала
/// отправляет одним потоком записи: при пробуждении дочитывает из базы всё после последнего отправленного —
/// так события не теряются и не дублируются, сколько бы их ни пришло за время синхронизации.
/// </summary>
public sealed class ClientConnection
{
    private const int BatchSize = 200;
    private const int MaxFrameSize = 4 * 1024 * 1024;

    private readonly WebSocket _socket;
    private readonly ServerDb _db;
    private readonly ChatLogic _logic;
    private readonly ILogger _log;
    private readonly ConcurrentQueue<Frame> _outgoing = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _closed = new();

    // Запрошенная синхронизация (-1 — нет новой). Курсор и «synced» трогает только поток записи.
    private long _requestedSince = -1;
    // Курсор журнала: после какого события отправлять. null — клиент ещё не попросил синхронизацию.
    private long? _cursor;
    private bool _syncedSent;

    public Guid DeviceId { get; }
    public string DeviceName { get; }
    public Guid AccountId { get; }
    public string AccountLogin { get; }

    public ClientConnection(WebSocket socket, Device device, Account account, ServerDb db, ChatLogic logic, ILogger log)
    {
        _socket = socket;
        _db = db;
        _logic = logic;
        _log = log;
        DeviceId = device.Id;
        DeviceName = device.Name;
        AccountId = account.Id;
        AccountLogin = account.Login;
    }

    /// <summary>Есть новые события в журнале.</summary>
    public void Wake() => _wake.Release();

    public void Enqueue(Frame frame)
    {
        _outgoing.Enqueue(frame);
        _wake.Release();
    }

    public void Close() => _closed.Cancel();

    public async Task RunAsync(CancellationToken stopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stopping, _closed.Token);
        var writer = Task.Run(() => WriteLoopAsync(linked.Token));
        try
        {
            await ReadLoopAsync(linked.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            await linked.CancelAsync();
            try
            {
                await writer;
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
            }
            if (_socket.State == WebSocketState.Open)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
                {
                }
            }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await _socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxFrameSize) return; // мусор или чужая программа
            } while (!result.EndOfMessage);
            if (result.MessageType != WebSocketMessageType.Text) continue;

            Frame? frame;
            try
            {
                frame = JsonSerializer.Deserialize<Frame>(message.GetBuffer().AsSpan(0, (int)message.Length), ServerProtocol.Json);
            }
            catch (JsonException)
            {
                Enqueue(new Frame { Op = Ops.Error, Error = ErrorCodes.BadRequest, Message = "Непонятный запрос" });
                continue;
            }
            if (frame == null) continue;
            await HandleAsync(frame);
        }
    }

    private async Task HandleAsync(Frame frame)
    {
        switch (frame.Op)
        {
            case Ops.Sync:
                Interlocked.Exchange(ref _requestedSince, Math.Max(0, frame.Since ?? 0));
                Wake();
                break;
            case Ops.Ping:
                Enqueue(new Frame { Op = Ops.Pong, Rid = frame.Rid });
                break;
            default:
                var reply = await _logic.HandleAsync(this, frame);
                if (reply != null) Enqueue(reply);
                break;
        }
    }

    private async Task WriteLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _wake.WaitAsync(ct);
            while (_outgoing.TryDequeue(out var frame))
                await SendAsync(frame, ct);
            var requested = Interlocked.Exchange(ref _requestedSince, -1);
            if (requested >= 0)
            {
                _cursor = requested;
                _syncedSent = false;
            }
            if (_cursor is not { } cursor) continue;

            // Дочитываем журнал пачками, пока он не кончится.
            while (true)
            {
                var events = _db.EventsAfter(AccountId, cursor, BatchSize);
                foreach (var e in events)
                {
                    using var data = JsonDocument.Parse(e.Data);
                    await SendAsync(new Frame { Op = Ops.Event, Seq = e.Seq, Kind = e.Kind, Data = data.RootElement.Clone() }, ct);
                    cursor = e.Seq;
                }
                _cursor = cursor;
                if (events.Count < BatchSize) break;
                // Между пачками — то, что пришло мимо журнала (например, ответы на запросы).
                while (_outgoing.TryDequeue(out var frame))
                    await SendAsync(frame, ct);
            }
            if (!_syncedSent)
            {
                _syncedSent = true;
                await SendAsync(new Frame { Op = Ops.Synced, Seq = cursor }, ct);
            }
        }
    }

    private async Task SendAsync(Frame frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, ServerProtocol.Json);
        await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }
}
