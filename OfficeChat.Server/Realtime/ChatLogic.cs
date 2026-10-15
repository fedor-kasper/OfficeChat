using System.Text.Json;
using OfficeChat.Protocol;
using OfficeChat.Server.Data;

namespace OfficeChat.Server.Realtime;

/// <summary>Ошибка в запросе клиента — уходит ему ответом, сервер работает дальше.</summary>
public sealed class ClientError(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Что сервер делает по запросам устройств: сообщения, правки, удаления, прочтение, группы, напоминания,
/// настройки и сигналы. Всё, что меняет переписку, пишется в журналы затронутых аккаунтов — так об этом
/// узнают все их устройства, в том числе те, что сейчас не в сети.
/// </summary>
public sealed class ChatLogic(ServerDb db, Hub hub, ILogger<ChatLogic> log)
{
    private const int MaxReminderJson = 64 * 1024;
    private const int MaxSettingValue = 64 * 1024;
    private const int MaxSignalPayload = 64 * 1024;

    public async Task<Frame?> HandleAsync(ClientConnection client, Frame frame)
    {
        try
        {
            switch (frame.Op)
            {
                case Ops.Ack:
                    await AckAsync(client, frame.Seq ?? 0);
                    return null;
                case Ops.Send:
                    await SendAsync(client, Data<MessageDto>(frame));
                    break;
                case Ops.Edit:
                    await EditAsync(client, Data<EditData>(frame));
                    break;
                case Ops.Delete:
                    await DeleteAsync(client, Data<DeleteData>(frame));
                    break;
                case Ops.Read:
                    await ReadAsync(client, Data<MessageIdsData>(frame));
                    break;
                case Ops.GroupCreate:
                    await CreateGroupAsync(client, Data<GroupCreateData>(frame));
                    break;
                case Ops.GroupRename:
                    await RenameGroupAsync(client, Data<GroupRenameData>(frame));
                    break;
                case Ops.GroupAdd:
                    await AddToGroupAsync(client, Data<GroupAddData>(frame));
                    break;
                case Ops.GroupLeave:
                    await LeaveGroupAsync(client, Data<GroupIdData>(frame));
                    break;
                case Ops.ReminderPut:
                    await PutReminderAsync(client, Data<ReminderPutData>(frame));
                    break;
                case Ops.ReminderDelete:
                    await DeleteReminderAsync(client, Data<ReminderIdData>(frame));
                    break;
                case Ops.ReminderDone:
                    await ReminderDoneAsync(client, Data<ReminderDoneData>(frame));
                    break;
                case Ops.SettingsPut:
                    await PutSettingAsync(client, Data<SettingsData>(frame));
                    break;
                case Ops.Signal:
                    Signal(client, Data<SignalData>(frame));
                    break;
                default:
                    throw new ClientError(ErrorCodes.BadRequest, $"Неизвестная операция «{frame.Op}»");
            }
            return new Frame { Op = Ops.Ok, Rid = frame.Rid };
        }
        catch (ClientError error)
        {
            return new Frame { Op = Ops.Error, Rid = frame.Rid, Error = error.Code, Message = error.Message };
        }
        catch (JsonException)
        {
            return new Frame { Op = Ops.Error, Rid = frame.Rid, Error = ErrorCodes.BadRequest, Message = "Неверные данные запроса" };
        }
    }

    private static T Data<T>(Frame frame) where T : class =>
        frame.Data is { } data && data.Deserialize<T>(ServerProtocol.Json) is { } value
            ? value
            : throw new ClientError(ErrorCodes.BadRequest, "В запросе нет данных");

    private static ClientError Bad(string message) => new(ErrorCodes.BadRequest, message);

    private static ClientError Forbidden(string message) => new(ErrorCodes.Forbidden, message);

    private static ClientError NotFound(string message) => new(ErrorCodes.NotFound, message);

    /// <summary>Изменение под общей блокировкой журнала; после — будим устройства затронутых аккаунтов.</summary>
    private async Task MutateAsync(Func<HashSet<Guid>> change)
    {
        HashSet<Guid> affected;
        await db.WriteLock.WaitAsync();
        try
        {
            affected = change();
        }
        finally
        {
            db.WriteLock.Release();
        }
        if (affected.Count > 0) hub.NotifyEvents(affected);
    }

    private string NameOf(Guid accountId) => db.FindAccount(accountId)?.DisplayName ?? "";

    // ---- Доставка и прочтение ----

    private Task AckAsync(ClientConnection client, long seq) => MutateAsync(() =>
    {
        var affected = new HashSet<Guid>();
        foreach (var (sender, ids) in db.MarkDelivered(client.AccountId, seq))
        {
            db.AppendEvents(new[] { sender }, EventKinds.Delivered, new MessageIdsData(ids, client.AccountId));
            affected.Add(sender);
        }
        return affected;
    });

    private Task ReadAsync(ClientConnection client, MessageIdsData data) => MutateAsync(() =>
    {
        var affected = new HashSet<Guid>();
        var marked = db.MarkRead(client.AccountId, data.MessageIds.Take(1000));
        foreach (var (sender, ids) in marked)
        {
            db.AppendEvents(new[] { sender }, EventKinds.Read, new MessageIdsData(ids, client.AccountId));
            affected.Add(sender);
        }
        // Своим устройствам — чтобы и на них сообщения стали прочитанными.
        var all = marked.Values.SelectMany(x => x).ToList();
        if (all.Count > 0)
        {
            db.AppendEvents(new[] { client.AccountId }, EventKinds.Read, new MessageIdsData(all, client.AccountId));
            affected.Add(client.AccountId);
        }
        return affected;
    });

    // ---- Сообщения ----

    private Task SendAsync(ClientConnection client, MessageDto message) => MutateAsync(() =>
    {
        if (message.Id == Guid.Empty) throw Bad("У сообщения нет Id");
        if (db.FindMessage(message.Id) is { } existing)
        {
            // Повтор (не дошёл ответ) — ничего не делаем; чужой Id — ошибка.
            if (existing.Message.SenderId == client.AccountId) return new HashSet<Guid>();
            throw Bad("Сообщение с таким Id уже есть");
        }
        if (message.Kind is not (MessageKinds.Text or MessageKinds.Image or MessageKinds.File))
            throw Bad("Неизвестный тип сообщения");
        if (message.Text.Length > ServerProtocol.MaxTextLength) throw Bad("Слишком длинное сообщение");
        if (message.Kind == MessageKinds.Text && string.IsNullOrWhiteSpace(message.Text)) throw Bad("Пустое сообщение");
        if ((message.RecipientId == null) == (message.GroupId == null))
            throw Bad("Нужно указать получателя или группу");

        if (message.RecipientId is { } recipient)
        {
            if (recipient == client.AccountId) throw Bad("Нельзя написать самому себе");
            if (!db.AccountExists(recipient)) throw NotFound("Получатель не найден");
        }
        else if (!db.IsGroupMember(message.GroupId!.Value, client.AccountId))
        {
            throw Forbidden("Вы не участник этой группы");
        }

        if (message.Kind != MessageKinds.Text)
        {
            if (message.FileId is not { } fileId || db.FindFile(fileId) is not { } file || file.OwnerId != client.AccountId)
                throw Bad("Файл не загружен");
            message.FileSize = file.Size;
            if (message.FileName.Length == 0) message.FileName = file.Name;
        }
        else
        {
            message.FileId = null;
            message.FileName = "";
            message.FileSize = 0;
        }

        message.SenderId = client.AccountId;
        message.SenderName = NameOf(client.AccountId);
        message.ServerTime = DateTimeOffset.UtcNow;
        if (message.SentAt == default) message.SentAt = message.ServerTime;
        if (!db.AddMessage(message)) return new HashSet<Guid>();

        var audience = db.AudienceOf(message);
        db.AppendEvents(audience, EventKinds.Message, message, message.Id, message.SenderId);
        return audience.ToHashSet();
    });

    private Task EditAsync(ClientConnection client, EditData data) => MutateAsync(() =>
    {
        var stored = OwnMessage(client, data.MessageId);
        var message = stored.Message;
        if (data.Text.Length > ServerProtocol.MaxTextLength) throw Bad("Слишком длинное сообщение");
        if (message.Kind == MessageKinds.Text && string.IsNullOrWhiteSpace(data.Text)) throw Bad("Пустое сообщение");
        if (message.Text == data.Text) return new HashSet<Guid>();
        message.Text = data.Text;
        db.UpdateMessage(message);
        var audience = db.AudienceOf(message);
        db.AppendEvents(audience, EventKinds.Edit, new EditData(message.Id, data.Text, DateTimeOffset.UtcNow, message.SenderId));
        return audience.ToHashSet();
    });

    private Task DeleteAsync(ClientConnection client, DeleteData data) => MutateAsync(() =>
    {
        var message = OwnMessage(client, data.MessageId).Message;
        db.MarkMessageDeleted(message.Id);
        var audience = db.AudienceOf(message);
        db.AppendEvents(audience, EventKinds.Delete, new DeleteData(message.Id, message.SenderId));
        return audience.ToHashSet();
    });

    private StoredMessage OwnMessage(ClientConnection client, Guid messageId)
    {
        var stored = db.FindMessage(messageId);
        if (stored == null || stored.Deleted) throw NotFound("Сообщение не найдено");
        if (stored.Message.SenderId != client.AccountId) throw Forbidden("Менять можно только свои сообщения");
        return stored;
    }

    // ---- Группы ----

    private static string GroupName(string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw Bad("Пустое название группы");
        return name.Length > ServerProtocol.MaxNameLength ? name[..ServerProtocol.MaxNameLength] : name;
    }

    private GroupDto GroupEvent(Guid groupId, string action, Guid actorId, List<Guid>? affected = null)
    {
        var group = db.FindGroup(groupId)!;
        return new GroupDto
        {
            Id = groupId,
            Name = group.Name,
            Members = group.Members.Select(m => new GroupMemberDto(m, NameOf(m))).OrderBy(m => m.DisplayName).ToList(),
            Action = action,
            ActorId = actorId,
            ActorName = NameOf(actorId),
            AffectedIds = affected ?? new List<Guid>(),
        };
    }

    private Task CreateGroupAsync(ClientConnection client, GroupCreateData data) => MutateAsync(() =>
    {
        if (data.Id == Guid.Empty) throw Bad("У группы нет Id");
        var name = GroupName(data.Name);
        var members = data.MemberIds.Append(client.AccountId).Distinct().ToList();
        if (members.Any(m => !db.AccountExists(m))) throw NotFound("Кого-то из участников нет на сервере");
        if (!db.CreateGroup(data.Id, name, client.AccountId, members))
        {
            // Повтор запроса — группа уже создана им же.
            if (db.IsGroupMember(data.Id, client.AccountId)) return new HashSet<Guid>();
            throw Bad("Группа с таким Id уже есть");
        }
        log.LogInformation("Создана группа «{Name}», участников: {Count}", name, members.Count);
        db.AppendEvents(members, EventKinds.Group, GroupEvent(data.Id, GroupActions.Created, client.AccountId));
        return members.ToHashSet();
    });

    private StoredGroup MemberGroup(ClientConnection client, Guid groupId)
    {
        var group = db.FindGroup(groupId) ?? throw NotFound("Группа не найдена");
        if (!group.Members.Contains(client.AccountId)) throw Forbidden("Вы не участник этой группы");
        return group;
    }

    private Task RenameGroupAsync(ClientConnection client, GroupRenameData data) => MutateAsync(() =>
    {
        var group = MemberGroup(client, data.Id);
        var name = GroupName(data.Name);
        if (name == group.Name) return new HashSet<Guid>();
        db.RenameGroup(group.Id, name);
        db.AppendEvents(group.Members, EventKinds.Group, GroupEvent(group.Id, GroupActions.Renamed, client.AccountId));
        return group.Members.ToHashSet();
    });

    private Task AddToGroupAsync(ClientConnection client, GroupAddData data) => MutateAsync(() =>
    {
        var group = MemberGroup(client, data.Id);
        var added = data.MemberIds.Distinct().Where(id => !group.Members.Contains(id)).ToList();
        if (added.Any(id => !db.AccountExists(id))) throw NotFound("Кого-то из участников нет на сервере");
        if (added.Count == 0) return new HashSet<Guid>();
        foreach (var id in added) db.AddGroupMember(group.Id, id);
        var members = db.GroupMembers(group.Id);
        db.AppendEvents(members, EventKinds.Group, GroupEvent(group.Id, GroupActions.Added, client.AccountId, added));
        return members.ToHashSet();
    });

    private Task LeaveGroupAsync(ClientConnection client, GroupIdData data) => MutateAsync(() =>
    {
        var group = MemberGroup(client, data.Id);
        db.RemoveGroupMember(group.Id, client.AccountId);
        // Ушедший тоже получает событие — чтобы группа пропала и на других его устройствах.
        db.AppendEvents(group.Members, EventKinds.Group,
            GroupEvent(group.Id, GroupActions.Left, client.AccountId, new List<Guid> { client.AccountId }));
        return group.Members.ToHashSet();
    });

    // ---- Напоминания ----

    private Task PutReminderAsync(ClientConnection client, ReminderPutData data) => MutateAsync(() =>
    {
        if (data.Id == Guid.Empty) throw Bad("У напоминания нет Id");
        if (data.Json.Length > MaxReminderJson) throw Bad("Слишком большое напоминание");
        var participants = data.ParticipantIds.Where(id => id != client.AccountId).Distinct().ToList();
        if (participants.Any(id => !db.AccountExists(id))) throw NotFound("Кого-то из участников нет на сервере");

        var existing = db.FindReminder(data.Id);
        if (existing != null && existing.CreatorId != client.AccountId) throw Forbidden("Менять напоминание может только автор");
        db.SaveReminder(new StoredReminder(data.Id, client.AccountId, participants, data.Json));

        var audience = participants.Append(client.AccountId).ToHashSet();
        db.AppendEvents(audience, EventKinds.Reminder, new ReminderData(data.Id, client.AccountId, data.Json));
        // Кого убрали из участников — у них напоминание пропадает.
        var removed = existing?.Participants.Where(id => !audience.Contains(id)).ToList() ?? new List<Guid>();
        if (removed.Count > 0) db.AppendEvents(removed, EventKinds.ReminderDelete, new ReminderIdData(data.Id));
        audience.UnionWith(removed);
        return audience;
    });

    private Task DeleteReminderAsync(ClientConnection client, ReminderIdData data) => MutateAsync(() =>
    {
        var reminder = db.FindReminder(data.Id) ?? throw NotFound("Напоминание не найдено");
        if (reminder.CreatorId != client.AccountId) throw Forbidden("Удалить у всех может только автор");
        db.DeleteReminder(reminder.Id);
        var audience = reminder.Participants.Append(reminder.CreatorId).ToHashSet();
        db.AppendEvents(audience, EventKinds.ReminderDelete, new ReminderIdData(reminder.Id));
        return audience;
    });

    private Task ReminderDoneAsync(ClientConnection client, ReminderDoneData data) => MutateAsync(() =>
    {
        var reminder = db.FindReminder(data.Id) ?? throw NotFound("Напоминание не найдено");
        if (reminder.CreatorId != client.AccountId && !reminder.Participants.Contains(client.AccountId))
            throw Forbidden("Это не ваше напоминание");
        var audience = reminder.Participants.Append(reminder.CreatorId).ToHashSet();
        db.AppendEvents(audience, EventKinds.ReminderDone, new ReminderDoneData(reminder.Id, data.Done, client.AccountId));
        return audience;
    });

    // ---- Настройки и сигналы ----

    private Task PutSettingAsync(ClientConnection client, SettingsData data) => MutateAsync(() =>
    {
        if (data.Key.Length is 0 or > 100) throw Bad("Неверное имя настройки");
        if (data.Value.Length > MaxSettingValue) throw Bad("Слишком большое значение настройки");
        db.SaveSetting(client.AccountId, data.Key, data.Value);
        db.AppendEvents(new[] { client.AccountId }, EventKinds.Settings, data);
        return new HashSet<Guid> { client.AccountId };
    });

    /// <summary>«печатает…», ходы в играх — не хранятся, только тем, кто сейчас в сети.</summary>
    private void Signal(ClientConnection client, SignalData data)
    {
        if (data.Type.Length is 0 or > 64) throw Bad("Неверный тип сигнала");
        if (data.Payload is { } payload && payload.GetRawText().Length > MaxSignalPayload) throw Bad("Слишком большой сигнал");
        List<Guid> targets;
        if (data.GroupId is { } groupId)
            targets = MemberGroup(client, groupId).Members.Where(m => m != client.AccountId).ToList();
        else if (data.To is { } to && to != client.AccountId && db.AccountExists(to))
            targets = new List<Guid> { to };
        else
            throw Bad("Нужно указать получателя или группу");

        hub.SendEphemeral(targets, new Frame
        {
            Op = Ops.Signal,
            From = client.AccountId,
            FromName = NameOf(client.AccountId),
            GroupId = data.GroupId,
            Type = data.Type,
            Payload = data.Payload,
        });
    }
}
