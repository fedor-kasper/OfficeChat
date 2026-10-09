using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace OfficeChat.Services;

/// <summary>
/// Прямая передача пакетов между компьютерами по TCP.
/// Каждый пакет — отдельное короткое соединение: отправитель шлёт пакет,
/// получатель отвечает подтверждением (ack). Нет подтверждения — пакет не доставлен.
/// Формат кадра: 4 байта длины (little-endian) + JSON в UTF-8.
/// </summary>
public sealed class MessagingService : IDisposable
{
    public const int MessagingPort = Models.Peer.DefaultMessagingPort;

    /// <summary>Порт, на котором реально слушаем (стандартный или свободный, если стандартный занят).</summary>
    public int Port { get; private set; }

    // Чтобы недоступный собеседник не засыпал лог: об ошибке отправки на адрес пишем не чаще раза в минуту.
    private readonly Dictionary<string, DateTime> _lastFailureLog = new();

    private const int MaxFrameSize = 1024 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(10);

    private readonly SynchronizationContext _uiContext;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    /// <summary>Пришёл пакет (вызывается в UI-потоке). Подтверждение отправителю уже отправлено.</summary>
    public event Action<ChatPacket, IPAddress>? PacketReceived;

    public MessagingService()
    {
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, MessagingPort);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            // Порт занят — обычно копией OfficeChat другого пользователя Windows на этом же компьютере.
            // Берём любой свободный; остальные узнают его из пакетов обнаружения.
            Log.Warn($"TCP-порт {MessagingPort} занят ({ex.SocketErrorCode}) — берём свободный");
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
        }
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Log.Info($"Сообщения: слушаем TCP-порт {Port}");
        _ = AcceptLoopAsync(_cts.Token);
    }

    /// <summary>Отправляет пакет и ждёт подтверждения. Возвращает true, если получатель его принял.</summary>
    public async Task<bool> SendAsync(IPAddress address, int port, ChatPacket packet)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(ExchangeTimeout);
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                connectTimeout.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(address, port, connectTimeout.Token);
            }

            var stream = client.GetStream();
            await WriteFrameAsync(stream, packet, timeout.Token);
            var ack = await ReadFrameAsync(stream, timeout.Token);
            var delivered = ack is { Type: ChatPacket.Ack } && ack.Id == packet.Id;
            if (!delivered) LogFailure(address, port, packet, "нет подтверждения");
            return delivered;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException
                                       or JsonException or ObjectDisposedException)
        {
            LogFailure(address, port, packet, ex is SocketException se ? se.SocketErrorCode.ToString() : ex.GetType().Name);
            return false;
        }
    }

    private void LogFailure(IPAddress address, int port, ChatPacket packet, string reason)
    {
        var key = $"{address}:{port}";
        lock (_lastFailureLog)
        {
            if (_lastFailureLog.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromMinutes(1))
                return;
            _lastFailureLog[key] = DateTime.UtcNow;
        }
        Log.Info($"Не удалось отправить «{packet.Type}» на {key}: {reason} (повторим позже)");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using var _ = client;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ExchangeTimeout);
        try
        {
            var stream = client.GetStream();
            var packet = await ReadFrameAsync(stream, timeout.Token).ConfigureAwait(false);
            if (packet == null || packet.App != ChatPacket.AppTag) return;

            await WriteFrameAsync(stream, new ChatPacket { Type = ChatPacket.Ack, Id = packet.Id }, timeout.Token)
                .ConfigureAwait(false);

            var from = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.MapToIPv4();
            if (packet.Type != ChatPacket.Ack)
                Log.Info($"Получен «{packet.Type}» от «{packet.FromName}» ({from}:{packet.FromPort})");
            _uiContext.Post(_ => PacketReceived?.Invoke(packet, from), null);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException
                                       or JsonException or ObjectDisposedException)
        {
            // Обрыв или мусор от чужой программы — просто закрываем соединение.
        }
    }

    private static async Task WriteFrameAsync(NetworkStream stream, ChatPacket packet, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(packet);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    private static async Task<ChatPacket?> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameSize)
            throw new IOException("Недопустимый размер пакета.");

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<ChatPacket>(body);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Stop();
        _cts.Dispose();
    }
}

/// <summary>Пакет чата, передаётся как JSON.</summary>
public sealed class ChatPacket
{
    public const string AppTag = "OfficeChat/1";

    /// <summary>Текстовое сообщение.</summary>
    public const string Message = "msg";

    /// <summary>Отметка «прочитано» для сообщений из <see cref="MessageIds"/>.</summary>
    public const string ReadReceipt = "read";

    /// <summary>Подтверждение приёма пакета с тем же <see cref="Id"/>.</summary>
    public const string Ack = "ack";

    // Крестики-нолики: все пакеты относятся к партии <see cref="GameId"/>.
    public const string GameInvite = "game-invite";
    public const string GameAccept = "game-accept";
    public const string GameDecline = "game-decline";
    /// <summary>Пригласивший передумал до ответа.</summary>
    public const string GameCancel = "game-cancel";
    /// <summary>Ход: клетка <see cref="Cell"/>, номер хода <see cref="MoveNumber"/>.</summary>
    public const string GameMove = "game-move";
    public const string GameResign = "game-resign";

    public string App { get; set; } = AppTag;
    public string Type { get; set; } = Message;
    public Guid Id { get; set; }

    public Guid From { get; set; }
    public string FromName { get; set; } = "";
    public string FromMachine { get; set; } = "";

    /// <summary>Порт сообщений отправителя — куда отвечать. 0 у старых версий: значит стандартный.</summary>
    public int FromPort { get; set; }

    public string Text { get; set; } = "";
    public DateTimeOffset SentAt { get; set; }
    public bool IsBroadcast { get; set; }

    public List<Guid>? MessageIds { get; set; }

    public Guid GameId { get; set; }
    public int Cell { get; set; }
    public int MoveNumber { get; set; }
}
