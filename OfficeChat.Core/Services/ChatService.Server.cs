using System.Net;
using System.Text.Json;
using OfficeChat.Client;
using OfficeChat.Models;
using OfficeChat.Protocol;

namespace OfficeChat.Services;

/// <summary>Вход в аккаунт на сервере: сервер, ключ устройства и кто мы.</summary>
public sealed record ServerSession(ServerApi Api, Guid AccountId, string Login);

/// <summary>
/// Работа через сервер (режим аккаунта). Наш Id — Id аккаунта, собеседники — аккаунты коллег.
/// Пока сервер доступен, всё (сообщения, файлы, правки, прочтение, группы, напоминания, закрепы)
/// идёт через него и синхронизируется на все устройства. Нет сервера — компьютеры общаются напрямую,
/// как раньше, а всё отправленное за это время уходит на сервер, когда он появится (очередь в базе).
/// События журнала сервера применяются теми же обработчиками, что и пакеты напрямую.
/// </summary>
public sealed partial class ChatService
{
    private const string ServerSeqKey = "server_seq";
    private const string PinnedSetting = "pinned";
    private const string MutedSetting = "muted";
    /// <summary>Сообщения старше этого при догонке журнала не всплывают уведомлениями (история).</summary>
    private static readonly TimeSpan FreshMessageAge = TimeSpan.FromMinutes(10);

    private ServerSession? _session;
    private ServerConnection? _connection;
    private readonly HashSet<Guid> _serverAccounts = new();
    private List<ServerOutboxItem> _serverOutbox = new();
    private bool _pumping;

    /// <summary>Работаем с аккаунтом на сервере.</summary>
    public bool IsServerMode => _session != null;

    /// <summary>Связь с сервером есть, журнал догнан.</summary>
    public bool IsServerConnected => _connection?.IsConnected == true;

    public ServerSession? Session => _session;

    /// <summary>Изменилась связь с сервером (для строки состояния).</summary>
    public event Action? ServerStateChanged;

    /// <summary>Сервер больше не принимает ключ устройства — нужно войти заново.</summary>
    public event Action<string>? SessionExpired;

    /// <summary>Подключить аккаунт (до <see cref="Start"/>).</summary>
    public void UseServer(ServerSession session)
    {
        _session = session;
        _serverOutbox = _store.LoadServerOutbox();
    }

    private void StartServer()
    {
        if (_session == null) return;
        _connection = new ServerConnection(_session.Api, () => long.TryParse(_store.GetState(ServerSeqKey), out var seq) ? seq : 0,
            HandleServerEventAsync);
        _connection.Connected += OnServerConnected;
        _connection.Disconnected += OnServerDisconnected;
        _connection.PresenceChanged += OnServerPresence;
        _connection.SignalReceived += OnServerSignal;
        _connection.DirectoryChanged += entry => AddServerAccount(entry);
        _connection.AuthFailed += reason => SessionExpired?.Invoke(reason);
        _connection.Start();
    }

    /// <summary>Собеседник — аккаунт на сервере (а не компьютер со старой версией без аккаунта).</summary>
    private bool IsServerPeer(Guid peerId) => _serverAccounts.Contains(peerId);

    /// <summary>Этому собеседнику сейчас пишем через сервер (а не напрямую).</summary>
    private bool ViaServer(Guid peerId) => IsServerConnected && IsServerPeer(peerId);

    private async void OnServerConnected()
    {
        Log.Info("Связь с сервером есть, журнал догнан");
        try
        {
            var accounts = await _session!.Api.AccountsAsync();
            foreach (var entry in accounts) AddServerAccount(entry);
        }
        catch (Exception ex) when (ex is HttpRequestException or ServerApiException or TaskCanceledException)
        {
            Log.Warn("Не удалось получить список сотрудников", ex);
        }
        // То, что ждало прямой доставки этим собеседникам, теперь доставит сервер.
        foreach (var item in _outbox.Where(o => IsServerPeer(o.PeerId)).ToList())
        {
            _outbox.Remove(item);
            _store.DeleteOutbox(item.Seq);
        }
        UpdateGroupStatuses();
        PresenceChanged?.Invoke();
        ServerStateChanged?.Invoke();
        _ = PumpServerOutboxAsync();
    }

    private void OnServerDisconnected()
    {
        Log.Info("Связь с сервером пропала — пока работаем напрямую");
        foreach (var peerId in _serverAccounts)
            if (_discovery.Find(peerId) is { } peer) peer.ServerOnline = false;
        UpdateGroupStatuses();
        PresenceChanged?.Invoke();
        ServerStateChanged?.Invoke();
        FlushAll();
    }

    /// <summary>Сотрудник из списка сервера — в контакты (всех видно, даже без переписки).</summary>
    private Contact? AddServerAccount(DirectoryEntry entry)
    {
        if (entry.Id == MyId) return null;
        _serverAccounts.Add(entry.Id);
        var peer = _discovery.Find(entry.Id) ?? _discovery.AddKnown(entry.Id, entry.DisplayName, "", IPAddress.None);
        if (entry.DisplayName.Length > 0) peer.Name = entry.DisplayName;
        peer.ServerOnline = entry.Online;
        var contact = GetOrAddContact(peer);
        if (contact.Messages.Count > 0) _store.SaveContact(peer);
        return contact;
    }

    private Contact ContactOfAccount(Guid id, string name)
    {
        if (_contactsByPeer.TryGetValue(id, out var contact)) return contact;
        return AddServerAccount(new DirectoryEntry(id, "", name, false, null))!;
    }

    private void OnServerPresence(Guid accountId, bool online)
    {
        if (_discovery.Find(accountId) is not { } peer) return;
        peer.ServerOnline = online;
        var contact = _contactsByPeer.GetValueOrDefault(accountId);
        if (contact != null)
            foreach (var message in contact.Messages.Where(m => m.CanCancel))
                message.PeerOffline = !peer.IsOnline;
        if (!online && contact != null) StopTyping(contact);
        UpdateGroupStatuses();
        PresenceChanged?.Invoke();
    }

    // ---- Очередь запросов к серверу ----

    /// <summary>Поставить запрос серверу в очередь (уйдёт сразу или, когда появится связь, по порядку).</summary>
    private void EnqueueServer(string op, object data)
    {
        if (_session == null) return;
        _serverOutbox.Add(_store.AddServerOutbox(op, JsonSerializer.Serialize(data, data.GetType(), ServerProtocol.Json)));
        _ = PumpServerOutboxAsync();
    }

    /// <summary>Запрос серверу из других служб (напоминания). Без аккаунта — ничего.</summary>
    public void ServerRequest(string op, object data) => EnqueueServer(op, data);

    /// <summary>Своё сообщение — на сервер (вложение сначала загружается).</summary>
    private void EnqueueServerSend(ChatMessage message) =>
        EnqueueServer(Ops.Send, new SendItem(message.Id));

    private sealed record SendItem(Guid MessageId);

    private async Task PumpServerOutboxAsync()
    {
        if (_pumping || _connection == null) return;
        _pumping = true;
        try
        {
            while (IsServerConnected && _serverOutbox.FirstOrDefault() is { } item)
            {
                try
                {
                    if (item.Op == Ops.Send)
                        await SendToServerAsync(JsonSerializer.Deserialize<SendItem>(item.Data, ServerProtocol.Json)!.MessageId);
                    else
                    {
                        using var data = JsonDocument.Parse(item.Data);
                        await _connection.OkAsync(item.Op, data.RootElement.Clone());
                    }
                }
                catch (ServerApiException ex)
                {
                    // Сервер отказал (нет такого получателя, нет прав) — повтор не поможет, идём дальше.
                    Log.Warn($"Сервер отклонил «{item.Op}»: {ex.Message}");
                }
                _serverOutbox.Remove(item);
                _store.DeleteServerOutbox(item.Seq);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskCanceledException or OperationCanceledException
                                       or HttpRequestException or IOException or System.Net.WebSockets.WebSocketException)
        {
            // Связь пропала — продолжим, когда появится.
            Log.Info($"Отправка на сервер прервалась: {ex.Message}");
        }
        finally
        {
            _pumping = false;
        }
    }

    private async Task SendToServerAsync(Guid messageId)
    {
        var (contact, message) = FindOwnMessage(messageId);
        if (contact == null || message == null) return; // удалили, пока ждало
        var dto = new MessageDto
        {
            Id = message.Id,
            RecipientId = contact.IsGroup ? null : contact.Peer!.Id,
            GroupId = contact.IsGroup ? contact.Key : null,
            Kind = message.Kind switch { MessageKind.Image => MessageKinds.Image, MessageKind.File => MessageKinds.File, _ => MessageKinds.Text },
            Text = message.Text,
            FileName = message.FileName,
            ReplyToId = message.ReplyToId,
            ReplyAuthor = message.ReplyAuthor == MyReplyAuthor ? _settings.DisplayName : message.ReplyAuthor,
            ReplyText = message.ReplyText,
            Quiet = message.IsQuiet,
            Sticker = message.IsSticker,
            Broadcast = message.IsBroadcast,
            SentAt = new DateTimeOffset(message.Timestamp),
        };

        if (message.IsImage || message.IsFile)
        {
            if (message.ServerFileId == null)
            {
                var path = message.IsImage ? message.ImagePath : message.FilePath;
                if (!File.Exists(path))
                {
                    Log.Error($"Файл вложения {path} пропал — на сервер не отправляем");
                    return;
                }
                var wasQueued = message.Status == MessageStatus.Queued;
                if (wasQueued) message.Status = MessageStatus.Sending;
                try
                {
                    var uploaded = await _session!.Api.UploadAsync(path, message.FileName,
                        new Progress<double>(p => message.TransferProgress = p));
                    message.ServerFileId = uploaded.Id;
                }
                finally
                {
                    message.TransferProgress = 0;
                    if (wasQueued && message.Status == MessageStatus.Sending) message.Status = MessageStatus.Queued;
                }
                SaveAnywhere(contact, message);
            }
            dto.FileId = message.ServerFileId;
        }

        await _connection!.OkAsync(Ops.Send, dto);
        if (message.Status is MessageStatus.Queued or MessageStatus.Sending)
        {
            message.Status = MessageStatus.Sent;
            message.PeerOffline = false;
            SaveAnywhere(contact, message);
        }
    }

    /// <summary>Своё сообщение по Id: в окне или в базе (и в какой переписке).</summary>
    private (Contact? Contact, ChatMessage? Message) FindOwnMessage(Guid id)
    {
        foreach (var contact in Conversations)
            if (contact.Messages.FirstOrDefault(m => m.Id == id) is { } loaded)
                return (contact, loaded);
        if (_store.LoadMessage(id) is not { } stored) return (null, null);
        return (ConversationByKey(stored.PeerId), stored.Message);
    }

    private Contact? ConversationByKey(Guid key) =>
        _groups.GetValueOrDefault(key) ?? _contactsByPeer.GetValueOrDefault(key);

    /// <summary>Сохранить сообщение, даже если оно не загружено в окно (старое).</summary>
    private void SaveAnywhere(Contact contact, ChatMessage message)
    {
        if (contact.Messages.Contains(message)) Save(contact, message);
        else if (_store.HasMessage(message.Id)) _store.SaveMessage(contact.Key, message);
    }

    // ---- События журнала ----

    private async Task HandleServerEventAsync(Frame frame)
    {
        var data = frame.Data!.Value;
        switch (frame.Kind)
        {
            case EventKinds.Message:
                await OnServerMessageAsync(data.Deserialize<MessageDto>(ServerProtocol.Json)!);
                break;
            case EventKinds.Edit:
                OnServerEdit(data.Deserialize<EditData>(ServerProtocol.Json)!);
                break;
            case EventKinds.Delete:
                OnServerDelete(data.Deserialize<DeleteData>(ServerProtocol.Json)!);
                break;
            case EventKinds.Delivered:
                OnServerReceipt(data.Deserialize<MessageIdsData>(ServerProtocol.Json)!, read: false);
                break;
            case EventKinds.Read:
                OnServerReceipt(data.Deserialize<MessageIdsData>(ServerProtocol.Json)!, read: true);
                break;
            case EventKinds.Group:
                OnServerGroup(data.Deserialize<GroupDto>(ServerProtocol.Json)!);
                break;
            case EventKinds.Reminder:
            case EventKinds.ReminderDelete:
            case EventKinds.ReminderDone:
                ServerReminderEvent?.Invoke(frame.Kind, data);
                break;
            case EventKinds.Settings:
                OnServerSetting(data.Deserialize<SettingsData>(ServerProtocol.Json)!);
                break;
        }
        if (frame.Seq is { } seq) _store.SetState(ServerSeqKey, seq.ToString());
    }

    /// <summary>Событие напоминаний с сервера (обрабатывает <see cref="ReminderService"/>).</summary>
    public event Action<string, JsonElement>? ServerReminderEvent;

    private async Task OnServerMessageAsync(MessageDto dto)
    {
        var conversation = dto.GroupId is { } groupId
            ? _groups.GetValueOrDefault(groupId)
            : dto.SenderId == MyId
                ? ContactOfAccount(dto.RecipientId!.Value, "")
                : ContactOfAccount(dto.SenderId, dto.SenderName);
        if (conversation == null)
        {
            Log.Warn($"Сообщение {dto.Id} в неизвестной группе {dto.GroupId} — пропускаем");
            return;
        }

        if (dto.SenderId == MyId)
        {
            await OnOwnMessageFromServerAsync(conversation, dto);
            return;
        }

        if (_store.HasMessage(dto.Id) || _store.WasSeen(dto.Id)) return; // уже пришло напрямую
        var packet = new ChatPacket
        {
            Type = dto.Kind switch { MessageKinds.Image => ChatPacket.Image, MessageKinds.File => ChatPacket.File, _ => ChatPacket.Message },
            Id = dto.Id,
            Text = dto.Text,
            SentAt = dto.SentAt,
            IsBroadcast = dto.Broadcast,
            Quiet = dto.Quiet,
            Sticker = dto.Sticker,
            FileName = dto.FileName,
            ReplyToId = dto.ReplyToId,
            ReplyAuthor = dto.ReplyAuthor,
            ReplyText = dto.ReplyText,
            From = dto.SenderId,
            FromName = dto.SenderName,
            GroupId = dto.GroupId ?? Guid.Empty,
        };
        if (dto.FileId is { } fileId)
        {
            packet.ReceivedPayloadPath = await DownloadAttachmentAsync(fileId, dto.FileName);
            if (packet.ReceivedPayloadPath == null) return;
        }

        // Старое (догоняем историю) не всплывает уведомлениями.
        var fresh = DateTimeOffset.UtcNow - dto.ServerTime < FreshMessageAge;
        _quietReceive = !fresh;
        try
        {
            ReceiveMessage(conversation, packet);
        }
        finally
        {
            _quietReceive = false;
        }
        if (_store.LoadMessage(dto.Id) is { Message: var saved } && saved.ServerFileId != dto.FileId)
        {
            saved.ServerFileId = dto.FileId;
            SaveAnywhere(conversation, conversation.Messages.FirstOrDefault(m => m.Id == dto.Id) ?? saved);
        }
    }

    // При догонке старой истории — без уведомлений и без отметки «непрочитано» у давно прочитанного.
    private bool _quietReceive;

    /// <summary>Своё сообщение: подтверждение отправки или отправленное с другого устройства.</summary>
    private async Task OnOwnMessageFromServerAsync(Contact conversation, MessageDto dto)
    {
        var (_, existing) = FindOwnMessage(dto.Id);
        if (existing != null)
        {
            if (existing.Status is MessageStatus.Queued or MessageStatus.Sending)
            {
                existing.Status = MessageStatus.Sent;
                SaveAnywhere(conversation, existing);
            }
            return;
        }

        var kind = dto.Kind switch { MessageKinds.Image => MessageKind.Image, MessageKinds.File => MessageKind.File, _ => MessageKind.Text };
        var filePath = "";
        if (dto.FileId is { } fileId)
        {
            var downloaded = await DownloadAttachmentAsync(fileId, dto.FileName);
            if (downloaded == null) return;
            try
            {
                if (kind == MessageKind.Image)
                {
                    Directory.CreateDirectory(ImageStore.Folder);
                    File.Move(downloaded, ImageStore.PathFor(dto.Id, dto.FileName), overwrite: true);
                }
                else
                {
                    filePath = FileStore.NewPath(dto.Id, dto.FileName);
                    File.Move(downloaded, filePath, overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Не удалось сохранить вложение «{dto.FileName}»", ex);
                FileStore.DeleteQuietly(downloaded);
                return;
            }
        }

        var message = new ChatMessage
        {
            Id = dto.Id,
            IsOutgoing = true,
            Kind = kind,
            Text = dto.Text,
            FileName = FileStore.SafeFileName(dto.FileName),
            ImagePath = kind == MessageKind.Image ? ImageStore.PathFor(dto.Id, dto.FileName) : "",
            FilePath = filePath,
            FileSize = dto.FileSize,
            Timestamp = dto.SentAt.LocalDateTime,
            IsBroadcast = dto.Broadcast,
            IsQuiet = dto.Quiet,
            IsSticker = dto.Sticker,
            ReplyToId = dto.ReplyToId,
            ReplyAuthor = dto.ReplyAuthor == _settings.DisplayName ? MyReplyAuthor : dto.ReplyAuthor,
            ReplyText = dto.ReplyText,
            Status = MessageStatus.Sent,
            ServerFileId = dto.FileId,
        };
        InsertByTime(conversation, message);
        if (conversation.IsGroup)
        {
            _store.AddReceipts(message.Id, conversation.Group!.Members.Where(m => m.Id != MyId).Select(m => m.Id).ToList());
            RefreshReceipts(message);
        }
        else
        {
            _store.SaveContact(conversation.Peer!);
        }
        Save(conversation, message);
    }

    /// <summary>Сообщение в переписку по времени (история может прийти не по порядку).</summary>
    private static void InsertByTime(Contact contact, ChatMessage message)
    {
        var index = contact.Messages.Count;
        while (index > 0 && contact.Messages[index - 1].Timestamp > message.Timestamp) index--;
        contact.Messages.Insert(index, message);
    }

    /// <summary>Скачать вложение во временный файл (null — не получилось, пропускаем сообщение).</summary>
    private async Task<string?> DownloadAttachmentAsync(Guid fileId, string name)
    {
        var temp = FileStore.NewIncomingTempFile(0);
        try
        {
            await _session!.Api.DownloadAsync(fileId, temp);
            return temp;
        }
        catch (Exception ex) when (ex is HttpRequestException or ServerApiException or IOException or TaskCanceledException)
        {
            Log.Warn($"Не удалось скачать вложение «{name}» с сервера", ex);
            FileStore.DeleteQuietly(temp);
            // Без связи — бросаем дальше: событие не будет подтверждено и придёт снова.
            if (ex is not ServerApiException) throw;
            return null;
        }
    }

    private void OnServerEdit(EditData data)
    {
        if (data.SenderId == MyId)
        {
            var (contact, message) = FindOwnMessage(data.MessageId);
            if (contact == null || message == null || message.Text == data.Text) return;
            message.Text = data.Text;
            message.IsEdited = true;
            SaveAnywhere(contact, message);
            return;
        }
        if (_store.LoadMessage(data.MessageId) is not { } stored || ConversationByKey(stored.PeerId) is not { } conversation) return;
        ApplyEdit(conversation, new ChatPacket { TargetId = data.MessageId, Text = data.Text, From = data.SenderId ?? Guid.Empty });
    }

    private void OnServerDelete(DeleteData data)
    {
        if (data.SenderId == MyId)
        {
            var (contact, message) = FindOwnMessage(data.MessageId);
            if (contact == null || message == null) return;
            if (contact.Messages.Contains(message)) RemoveMessage(contact, message);
            else _store.DeleteMessage(message.Id);
            return;
        }
        if (_store.LoadMessage(data.MessageId) is not { } stored || ConversationByKey(stored.PeerId) is not { } conversation)
        {
            // Удаление раньше самого сообщения — не покажем его, когда придёт.
            _store.MarkSeen(data.MessageId);
            return;
        }
        ApplyDelete(conversation, new ChatPacket { TargetId = data.MessageId, From = data.SenderId ?? Guid.Empty });
    }

    /// <summary>«Доставлено»/«прочитано»: чужие отметки на наших сообщениях или свои — с другого устройства.</summary>
    private void OnServerReceipt(MessageIdsData data, bool read)
    {
        var who = data.AccountId ?? Guid.Empty;
        if (who == MyId)
        {
            if (read) MarkReadFromOtherDevice(data.MessageIds);
            return;
        }
        foreach (var id in data.MessageIds)
        {
            var (contact, message) = FindOwnMessage(id);
            if (contact == null || message == null) continue;
            if (contact.IsGroup)
            {
                _store.SetReceipt(id, who, read ? 2 : 1);
                RefreshReceipts(message);
                SaveAnywhere(contact, message);
            }
            else
            {
                var status = read ? MessageStatus.Read : MessageStatus.Delivered;
                if (message.Status == MessageStatus.Read || (!read && message.Status == MessageStatus.Delivered)) continue;
                message.Status = status;
                SaveAnywhere(contact, message);
            }
        }
    }

    /// <summary>Прочитали на другом своём устройстве — здесь тоже не считаем непрочитанным.</summary>
    private void MarkReadFromOtherDevice(List<Guid> ids)
    {
        var set = ids.ToHashSet();
        var changed = false;
        foreach (var contact in Conversations)
        {
            foreach (var message in contact.Messages.Where(m => !m.IsOutgoing && !m.IsRead && set.Contains(m.Id)))
            {
                message.IsRead = true;
                message.ReadReceiptSent = true;
                Save(contact, message);
                if (contact.UnreadCount > 0) contact.UnreadCount--;
                changed = true;
            }
        }
        if (changed) UnreadChanged?.Invoke();
    }

    private void OnServerSetting(SettingsData data)
    {
        List<Guid>? ids;
        try
        {
            ids = JsonSerializer.Deserialize<List<Guid>>(data.Value);
        }
        catch (JsonException)
        {
            return;
        }
        if (ids == null) return;
        switch (data.Key)
        {
            case PinnedSetting:
                _settings.PinnedChats = ids;
                foreach (var contact in Conversations) contact.IsPinned = ids.Contains(contact.Key);
                break;
            case MutedSetting:
                _settings.MutedChats = ids;
                foreach (var contact in Conversations) contact.IsMuted = ids.Contains(contact.Key);
                UnreadChanged?.Invoke();
                break;
            default:
                return;
        }
        SettingsService.Save(_settings);
    }

    // ---- Сигналы: «печатает…», игры ----

    private const string TypingSignal = "typing";
    private const string PacketSignal = "packet";

    private void OnServerSignal(Frame frame)
    {
        if (frame.From is not { } from || from == MyId) return;
        var sender = ContactOfAccount(from, frame.FromName ?? "");
        switch (frame.Type)
        {
            case TypingSignal:
                var conversation = frame.GroupId is { } groupId ? _groups.GetValueOrDefault(groupId) : sender;
                if (conversation != null)
                    ShowTyping(conversation, conversation.IsGroup ? $"{sender.Title} печатает…" : "печатает…");
                break;
            case PacketSignal when frame.Payload is { } payload:
                // Служебные пакеты (игры) через сервер — как будто пришли напрямую.
                var packet = payload.Deserialize<ChatPacket>();
                if (packet == null || packet.From != from || !packet.Type.StartsWith("game-", StringComparison.Ordinal)) return;
                GamePacketReceived?.Invoke(sender, packet);
                break;
        }
    }

    /// <summary>Отправить служебный пакет через сервер (игры). false — собеседника нет в сети.</summary>
    private bool SendPacketViaServer(Contact contact, ChatPacket packet)
    {
        if (!contact.Peer!.ServerOnline) return false;
        Stamp(packet);
        _connection!.Signal(contact.Peer.Id, null, PacketSignal, JsonSerializer.SerializeToElement(packet));
        return true;
    }

    // ---- Группы ----

    private void OnServerGroup(GroupDto dto)
    {
        var members = dto.Members.Select(m => new GroupMember(m.Id, m.Id == MyId ? _settings.DisplayName : m.DisplayName)).ToList();
        foreach (var member in dto.Members.Where(m => m.Id != MyId))
            ContactOfAccount(member.Id, member.DisplayName);
        var actor = dto.ActorId == MyId ? "Вы" : dto.ActorName;
        var amMember = members.Any(m => m.Id == MyId);

        if (!_groups.TryGetValue(dto.Id, out var contact))
        {
            // Группы у нас нет: создаём, если мы в ней (в том числе созданную на другом своём устройстве).
            if (!amMember) return;
            var group = new ChatGroup { Id = dto.Id, Name = CleanGroupName(dto.Name), Members = members };
            _store.ForgetLeftGroup(group.Id);
            _store.SaveGroup(group);
            contact = AddGroupContact(group);
            var others = members.Where(m => m.Id != MyId).ToList();
            AddServiceRecord(contact, dto.ActorId == MyId
                ? $"Вы создали группу «{group.Name}»: {Names(others)}"
                : dto.Action == GroupActions.Created
                    ? $"{actor} создал(а) группу «{group.Name}»" + (others.Count > 1 ? $". Участники: вы, {Names(others.Where(m => m.Id != dto.ActorId))}" : " с вами")
                    : $"{actor} добавил(а) вас в группу «{group.Name}»",
                notify: dto.ActorId != MyId);
            return;
        }

        var local = contact.Group!;
        if (!amMember)
        {
            // Мы вышли (с другого устройства) — убираем группу и здесь.
            RemoveGroupLocally(contact);
            return;
        }
        // Сделанное на этом устройстве уже видно — запись добавляем, только если что-то действительно поменялось.
        switch (dto.Action)
        {
            case GroupActions.Renamed when local.Name != dto.Name:
                AddServiceRecord(contact, $"{actor} переименовал(а) группу в «{CleanGroupName(dto.Name)}»");
                break;
            case GroupActions.Added:
                var added = members.Where(m => !local.HasMember(m.Id)).ToList();
                if (added.Count > 0) AddServiceRecord(contact, $"{actor} добавил(а): {Names(added)}");
                break;
            case GroupActions.Left:
                var left = local.Members.Where(m => members.All(x => x.Id != m.Id)).ToList();
                foreach (var member in left) AddServiceRecord(contact, $"{member.Name} вышел(-ла) из группы");
                break;
        }
        local.Name = CleanGroupName(dto.Name);
        local.Members = members;
        _store.SaveGroup(local);
        UpdateGroupStatus(contact);
    }

    private void RemoveGroupLocally(Contact contact)
    {
        _store.LeaveGroup(contact.Key);
        _groups.Remove(contact.Key);
        Contacts.Remove(contact);
        contact.Messages.Clear();
        if (contact.UnreadCount == 0) return;
        contact.UnreadCount = 0;
        UnreadChanged?.Invoke();
    }

    // ---- Для настроек: закрепы и «без уведомлений» на всех устройствах ----

    private void SyncSetting(string key, List<Guid> ids)
    {
        if (IsServerMode) EnqueueServer(Ops.SettingsPut, new SettingsData(key, JsonSerializer.Serialize(ids)));
    }

    private async Task DisposeServerAsync()
    {
        if (_connection != null) await _connection.DisposeAsync();
    }
}
