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
    private readonly Dictionary<string, SendFailure> _lastFailure = new();

    private const int MaxFrameSize = 1024 * 1024;
    // Двоичное вложение (изображение) идёт сразу после JSON-заголовка отдельным блоком.
    private const long MaxPayloadSize = ImageStore.MaxBytes;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    // С запасом на передачу изображения до 20 МБ по медленной сети.
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(60);

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
        var connected = false;
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                connectTimeout.CancelAfter(ConnectTimeout);
                await client.ConnectAsync(address, port, connectTimeout.Token);
            }
            connected = true;

            var stream = client.GetStream();
            await WriteFrameAsync(stream, packet, timeout.Token);
            var ack = await ReadFrameAsync(stream, timeout.Token);
            var delivered = ack is { Type: ChatPacket.Ack } && ack.Id == packet.Id;
            if (!delivered) RecordFailure(address, port, packet, SendFailure.Other, "нет подтверждения");
            else lock (_lastFailure) _lastFailure.Remove(Key(address, port));
            return delivered;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException
                                       or JsonException or ObjectDisposedException)
        {
            var (kind, reason) = Classify(ex, connected);
            RecordFailure(address, port, packet, kind, reason);
            return false;
        }
    }

    /// <summary>Почему не удалась последняя отправка на этот адрес (None — последняя удалась или не было).</summary>
    public SendFailure LastFailureFor(IPAddress address, int port)
    {
        lock (_lastFailure)
            return _lastFailure.GetValueOrDefault(Key(address, port));
    }

    private static string Key(IPAddress address, int port) => $"{address}:{port}";

    /// <summary>Переводим сетевую ошибку в понятную причину — её видно в логе и в статусе сообщения.</summary>
    private static (SendFailure Kind, string Reason) Classify(Exception ex, bool connected)
    {
        if (!connected)
        {
            if (ex is OperationCanceledException || ex is SocketException { SocketErrorCode: SocketError.TimedOut })
                return (SendFailure.NoAnswer,
                    "компьютер не отвечает на подключение — входящие соединения, скорее всего, " +
                    "блокирует брандмауэр на компьютере получателя (или компьютер выключен)");
            if (ex is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                return (SendFailure.Refused, "подключение отклонено — OfficeChat у получателя не запущен");
            if (ex is SocketException { SocketErrorCode: SocketError.HostUnreachable or SocketError.NetworkUnreachable } net)
                return (SendFailure.NoAnswer, $"компьютер недоступен по сети ({net.SocketErrorCode})");
        }
        var code = ex is SocketException se ? se.SocketErrorCode.ToString() : ex.GetType().Name;
        return (SendFailure.Other, connected ? $"соединение прервалось во время передачи ({code})" : code);
    }

    private void RecordFailure(IPAddress address, int port, ChatPacket packet, SendFailure kind, string reason)
    {
        var key = Key(address, port);
        lock (_lastFailure) _lastFailure[key] = kind;
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
        if (packet.Payload is { Length: > 0 } payload)
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
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
        var packet = JsonSerializer.Deserialize<ChatPacket>(body);

        if (packet is { PayloadLength: > 0 })
        {
            if (packet.PayloadLength > MaxPayloadSize)
                throw new IOException("Слишком большое вложение.");
            packet.Payload = new byte[packet.PayloadLength];
            await stream.ReadExactlyAsync(packet.Payload, ct).ConfigureAwait(false);
        }
        return packet;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener?.Stop();
        _cts.Dispose();
    }
}

public enum SendFailure
{
    None,
    /// <summary>Нет ответа на подключение — обычно брандмауэр получателя или компьютер выключен.</summary>
    NoAnswer,
    /// <summary>Компьютер ответил отказом — программа не запущена.</summary>
    Refused,
    Other,
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

    /// <summary>Изображение: файл <see cref="FileName"/>, байты — в <see cref="Payload"/>, подпись — в <see cref="Text"/>.</summary>
    public const string Image = "image";

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

    public string FileName { get; set; } = "";

    /// <summary>Размер двоичного вложения, которое идёт следом за JSON.</summary>
    public long PayloadLength { get; set; }

    /// <summary>Само вложение — в JSON не попадает, передаётся отдельным блоком.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public byte[]? Payload
    {
        get => _payload;
        set
        {
            _payload = value;
            PayloadLength = value?.Length ?? 0;
        }
    }
    private byte[]? _payload;

    public Guid GameId { get; set; }
    public int Cell { get; set; }
    public int MoveNumber { get; set; }
}
