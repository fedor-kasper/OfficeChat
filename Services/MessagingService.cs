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
    // Двоичное вложение (изображение, файл) идёт сразу после JSON-заголовка отдельным блоком.
    private const long MaxPayloadSize = FileStore.MaxBytes;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(60);
    // На вложение даём время из расчёта медленной сети: не меньше 256 КБ/с.
    private const long MinBytesPerSecond = 256 * 1024;
    private const int CopyBufferSize = 256 * 1024;

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

    /// <summary>
    /// Отправляет пакет и ждёт подтверждения. Возвращает true, если получатель его принял.
    /// <paramref name="progress"/> — доля отправленного вложения (0…1), если оно есть.
    /// </summary>
    public async Task<bool> SendAsync(IPAddress address, int port, ChatPacket packet, IProgress<double>? progress = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(TimeoutFor(packet.PayloadLength));
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
            await WriteFrameAsync(stream, packet, timeout.Token, progress);
            var ack = await ReadHeaderAsync(stream, timeout.Token);
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

    private static TimeSpan TimeoutFor(long payloadLength) =>
        ExchangeTimeout + TimeSpan.FromSeconds((double)payloadLength / MinBytesPerSecond);

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
        string? tempFile = null;
        try
        {
            var stream = client.GetStream();
            var packet = await ReadHeaderAsync(stream, timeout.Token).ConfigureAwait(false);
            if (packet == null || packet.App != ChatPacket.AppTag) return;
            var from = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.MapToIPv4();

            if (packet.PayloadLength > 0)
            {
                // Вложение пишем прямо на диск — файл может быть больше, чем разумно держать в памяти.
                tempFile = FileStore.NewIncomingTempFile(packet.PayloadLength);
                timeout.CancelAfter(TimeoutFor(packet.PayloadLength));
                await using (var file = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None,
                                 CopyBufferSize, useAsync: true))
                    await CopyExactlyAsync(stream, file, packet.PayloadLength, null, timeout.Token).ConfigureAwait(false);
                packet.ReceivedPayloadPath = tempFile;
            }

            // Подтверждаем только когда всё, включая вложение, уже на диске.
            await WriteFrameAsync(stream, new ChatPacket { Type = ChatPacket.Ack, Id = packet.Id }, timeout.Token)
                .ConfigureAwait(false);
            tempFile = null; // теперь файлом распоряжается получатель пакета

            // «печатает…» приходит каждые несколько секунд — в лог не пишем.
            if (packet.Type != ChatPacket.Typing)
                Log.Info($"Получен «{packet.Type}» от «{packet.FromName}» ({from}:{packet.FromPort})" +
                         (packet.PayloadLength > 0 ? $", вложение {packet.PayloadLength / 1024} КБ" : ""));
            _uiContext.Post(_ => PacketReceived?.Invoke(packet, from), null);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException
                                       or JsonException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // Обрыв или мусор от чужой программы — просто закрываем соединение.
            if (ex is IOException { Message: var message } && message.StartsWith("Недостаточно", StringComparison.Ordinal))
                Log.Warn(message);
        }
        finally
        {
            if (tempFile != null) FileStore.DeleteQuietly(tempFile);
        }
    }

    private static async Task WriteFrameAsync(NetworkStream stream, ChatPacket packet, CancellationToken ct,
        IProgress<double>? progress = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(packet);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);

        if (packet.Payload is { Length: > 0 } payload)
        {
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        }
        else if (packet.PayloadPath is { } path && packet.PayloadLength > 0)
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                CopyBufferSize, useAsync: true);
            await CopyExactlyAsync(file, stream, packet.PayloadLength, progress, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Копирует ровно <paramref name="length"/> байт, сообщая долю скопированного.</summary>
    private static async Task CopyExactlyAsync(Stream source, Stream target, long length,
        IProgress<double>? progress, CancellationToken ct)
    {
        var buffer = new byte[CopyBufferSize];
        long done = 0;
        var lastReported = -1;
        while (done < length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - done)), ct)
                .ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Передача оборвалась.");
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;

            var percent = (int)(done * 100 / length);
            if (progress != null && percent != lastReported)
            {
                lastReported = percent;
                progress.Report((double)done / length);
            }
        }
    }

    /// <summary>Читает JSON-заголовок пакета (вложение, если есть, читается отдельно).</summary>
    private static async Task<ChatPacket?> ReadHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxFrameSize)
            throw new IOException("Недопустимый размер пакета.");

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        var packet = JsonSerializer.Deserialize<ChatPacket>(body);
        if (packet is { PayloadLength: > MaxPayloadSize })
            throw new IOException("Слишком большое вложение.");
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

    /// <summary>Отправитель набирает сообщение (показываем «печатает…» несколько секунд).</summary>
    public const string Typing = "typing";

    /// <summary>Отправитель изменил своё сообщение <see cref="TargetId"/>: новый текст — в <see cref="Text"/>.</summary>
    public const string Edit = "edit";

    /// <summary>Отправитель удалил своё сообщение <see cref="TargetId"/> у всех.</summary>
    public const string Delete = "delete";

    /// <summary>
    /// Событие группы <see cref="GroupId"/>: <see cref="GroupAction"/> — создана, добавлены участники,
    /// переименована или отправитель вышел. В пакете — актуальные название и состав.
    /// </summary>
    public const string GroupUpdate = "group";

    public const string GroupCreated = "create";
    public const string GroupMembersAdded = "add";
    public const string GroupRenamed = "rename";
    public const string GroupLeft = "leave";

    /// <summary>Просьба прислать свою (более новую) сборку программы.</summary>
    public const string UpdateRequest = "update-request";

    /// <summary>Сборка программы вложением: версия — в <see cref="Text"/>, система — в <see cref="FileName"/>.</summary>
    public const string UpdatePackage = "update-package";

    /// <summary>Напоминание для участника (новое или изменённое автором): данные — JSON в <see cref="Text"/>.</summary>
    public const string ReminderShare = "reminder";

    /// <summary>Автор удалил напоминание <see cref="TargetId"/>.</summary>
    public const string ReminderDelete = "reminder-delete";

    /// <summary>Отправитель выполнил напоминание <see cref="TargetId"/> («done») или снова открыл («undone»).</summary>
    public const string ReminderDone = "reminder-done";

    /// <summary>Подтверждение приёма пакета с тем же <see cref="Id"/>.</summary>
    public const string Ack = "ack";

    // Мини-игры: все пакеты относятся к партии <see cref="GameId"/>; вид игры — в приглашении (<see cref="GameKind"/>).
    public const string GameInvite = "game-invite";
    public const string GameAccept = "game-accept";
    public const string GameDecline = "game-decline";
    /// <summary>Пригласивший передумал до ответа.</summary>
    public const string GameCancel = "game-cancel";
    /// <summary>
    /// Ход номер <see cref="MoveNumber"/>: в крестиках-ноликах — клетка <see cref="Cell"/>, в шашках — путь
    /// шашки <see cref="Cells"/>, в морском бое — выстрел по клетке <see cref="Cell"/>.
    /// </summary>
    public const string GameMove = "game-move";
    /// <summary>Морской бой: флот расставлен.</summary>
    public const string GameReady = "game-ready";
    /// <summary>Морской бой: ответ на выстрел <see cref="MoveNumber"/> — <see cref="ShotResult"/>, клетки потопленного корабля в <see cref="Cells"/>.</summary>
    public const string GameShotResult = "game-result";
    public const string GameResign = "game-resign";

    /// <summary>Изображение: имя <see cref="FileName"/>, байты — вложением, подпись — в <see cref="Text"/>.</summary>
    public const string Image = "image";

    /// <summary>Файл любого типа: имя <see cref="FileName"/>, содержимое — вложением, подпись — в <see cref="Text"/>.</summary>
    public const string File = "file";

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

    /// <summary>Тихое сообщение — получатель показывает его маленьким уведомлением.</summary>
    public bool Quiet { get; set; }

    public List<Guid>? MessageIds { get; set; }

    /// <summary>Ответ на сообщение: его Id, автор и начало текста (чтобы показать цитату, даже если оригинала нет).</summary>
    public Guid? ReplyToId { get; set; }
    public string ReplyAuthor { get; set; } = "";
    public string ReplyText { get; set; } = "";

    /// <summary>Сообщение, к которому относится правка или удаление.</summary>
    public Guid TargetId { get; set; }

    /// <summary>
    /// Группа, к которой относится пакет (сообщение, правка, «прочитано», «печатает…»). Пусто — личная переписка.
    /// С каждым сообщением приходят название и состав — по ним группу можно создать, если её ещё нет.
    /// </summary>
    public Guid GroupId { get; set; }
    public string GroupName { get; set; } = "";
    public List<Models.GroupMember>? GroupMembers { get; set; }
    public string GroupAction { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsGroup => GroupId != Guid.Empty;

    public string FileName { get; set; } = "";

    /// <summary>Размер двоичного вложения, которое идёт следом за JSON.</summary>
    public long PayloadLength { get; set; }

    /// <summary>Вложение из памяти — в JSON не попадает, передаётся отдельным блоком.</summary>
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

    /// <summary>Вложение из файла на диске — отправляется потоком, не загружаясь в память.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? PayloadPath
    {
        get => _payloadPath;
        set
        {
            _payloadPath = value;
            PayloadLength = value != null ? new FileInfo(value).Length : 0;
        }
    }
    private string? _payloadPath;

    /// <summary>У получателя: временный файл с принятым вложением (его нужно перенести на место или удалить).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ReceivedPayloadPath { get; set; }

    public Guid GameId { get; set; }
    public string GameKind { get; set; } = "";
    public int Cell { get; set; }
    public int MoveNumber { get; set; }
    public List<int>? Cells { get; set; }
    public int ShotResult { get; set; }
}
