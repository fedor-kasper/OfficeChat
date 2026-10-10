using OfficeChat.Models;

namespace OfficeChat.Services;

/// <summary>
/// Групповые чаты. Сервера нет: у каждого участника своя копия группы, а своё сообщение
/// мы доставляем каждому участнику отдельно и отмечаем, кому оно уже дошло и кто его прочитал.
/// Состав и название согласуются служебными пакетами (они идут через ту же очередь, что правки).
/// </summary>
public sealed partial class ChatService
{
    public const int MaxGroupNameLength = 60;

    // Групповые переписки по Id группы.
    private readonly Dictionary<Guid, Contact> _groups = new();

    /// <summary>Все известные компьютеры (кого можно позвать в группу) — по алфавиту.</summary>
    public IEnumerable<Contact> People =>
        _contactsByPeer.Values.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>В сети ли участник группы.</summary>
    public bool IsPeerOnline(Guid id) => _discovery.Find(id)?.IsOnline == true;

    private GroupMember Me => new(_settings.UserId, _settings.DisplayName);

    private void LoadGroups()
    {
        foreach (var group in _store.LoadGroups())
            LoadMessages(AddGroupContact(group));
    }

    private Contact AddGroupContact(ChatGroup group)
    {
        var contact = Contact.For(group);
        contact.IsMuted = _settings.MutedChats.Contains(group.Id);
        _groups.Add(group.Id, contact);
        InsertSorted(contact);
        UpdateGroupStatus(contact);
        return contact;
    }

    // ---- Действия пользователя ----

    /// <summary>Создаёт группу с выбранными людьми и сообщает им об этом.</summary>
    public Contact CreateGroup(string name, IEnumerable<Contact> people)
    {
        var group = new ChatGroup
        {
            Id = Guid.NewGuid(),
            Name = CleanGroupName(name),
            Members = people.Where(p => p.IsPerson).Select(p => new GroupMember(p.Peer!.Id, p.Title)).Prepend(Me).ToList(),
        };
        _store.SaveGroup(group);
        var contact = AddGroupContact(group);
        AddServiceRecord(contact, $"Вы создали группу «{group.Name}»: {Names(group.Members.Skip(1))}");
        SendGroupUpdate(contact, ChatPacket.GroupCreated);
        Log.Info($"Создана группа «{group.Name}» ({group.Members.Count} участников)");
        return contact;
    }

    /// <summary>Добавляет людей в группу (сделать это может любой участник).</summary>
    public void AddGroupMembers(Contact contact, IEnumerable<Contact> people)
    {
        var group = contact.Group!;
        var added = people.Where(p => p.IsPerson && !group.HasMember(p.Peer!.Id))
            .Select(p => new GroupMember(p.Peer!.Id, p.Title)).ToList();
        if (added.Count == 0) return;

        group.Members.AddRange(added);
        _store.SaveGroup(group);
        UpdateGroupStatus(contact);
        AddServiceRecord(contact, $"Вы добавили: {Names(added)}");
        // Новым участникам пакет создаст группу, остальным — сообщит о пополнении.
        SendGroupUpdate(contact, ChatPacket.GroupMembersAdded);
    }

    public void RenameGroup(Contact contact, string name)
    {
        var group = contact.Group!;
        name = CleanGroupName(name);
        if (name == group.Name) return;

        group.Name = name;
        _store.SaveGroup(group);
        AddServiceRecord(contact, $"Вы переименовали группу в «{name}»");
        SendGroupUpdate(contact, ChatPacket.GroupRenamed);
    }

    /// <summary>Выход из группы: остальные узнают об этом, у нас группа удаляется вместе с перепиской.</summary>
    public void LeaveGroup(Contact contact)
    {
        var group = contact.Group!;
        group.Members.RemoveAll(m => m.Id == MyId);
        SendGroupUpdate(contact, ChatPacket.GroupLeft);

        _store.LeaveGroup(group.Id);
        _groups.Remove(group.Id);
        Contacts.Remove(contact);
        contact.Messages.Clear();
        if (contact.UnreadCount != 0)
        {
            contact.UnreadCount = 0;
            UnreadChanged?.Invoke();
        }
        Log.Info($"Вышли из группы «{group.Name}»");
    }

    /// <summary>Рассылает остальным участникам событие группы вместе с актуальными названием и составом.</summary>
    private void SendGroupUpdate(Contact contact, string action)
    {
        var group = contact.Group!;
        foreach (var member in group.Members.Where(m => m.Id != MyId))
        {
            EnqueuePacket(member.Id, new ChatPacket
            {
                Type = ChatPacket.GroupUpdate,
                Id = Guid.NewGuid(),
                GroupId = group.Id,
                GroupName = group.Name,
                GroupMembers = group.Members.ToList(),
                GroupAction = action,
            });
        }
    }

    // ---- Отправка сообщений ----

    /// <summary>Своё новое сообщение в группе: запоминаем, кому его доставить, и начинаем рассылку.</summary>
    private void PostToGroup(Contact contact, ChatMessage message)
    {
        var recipients = contact.Group!.Members.Where(m => m.Id != MyId).Select(m => m.Id).ToList();
        if (recipients.Count == 0)
        {
            // В группе никого, кроме нас, — отправлять некому.
            message.Status = MessageStatus.Delivered;
            Save(contact, message);
            return;
        }
        Save(contact, message);
        _store.AddReceipts(message.Id, recipients);
        RefreshReceipts(message);
        foreach (var peerId in recipients)
            _ = FlushAsync(peerId);
    }

    /// <summary>
    /// Доставляет участнику по порядку свои сообщения из всех групп, которые до него ещё не дошли.
    /// false — участник недоступен, продолжим при следующей попытке.
    /// </summary>
    private async Task<bool> FlushGroupMessagesAsync(Peer peer)
    {
        foreach (var (messageId, groupId) in _store.PendingGroupDeliveries(peer.Id))
        {
            if (!_groups.TryGetValue(groupId, out var contact)) continue;
            var group = contact.Group!;
            var message = contact.Messages.FirstOrDefault(m => m.Id == messageId) ?? _store.LoadMessage(messageId)?.Message;
            if (message == null) continue;

            var packet = BuildMessagePacket(message);
            if (packet == null)
            {
                if (contact.Messages.Contains(message)) RemoveMessage(contact, message);
                else _store.DeleteMessage(message.Id);
                continue;
            }
            packet.GroupId = group.Id;
            packet.GroupName = group.Name;
            packet.GroupMembers = group.Members.ToList();

            message.Status = MessageStatus.Sending;
            message.TransferProgress = 0;
            var delivered = await _messaging.SendAsync(peer.Address, peer.Port, packet,
                packet.PayloadLength > 0 ? new Progress<double>(p => message.TransferProgress = p) : null);
            message.TransferProgress = 0;
            if (!delivered)
            {
                RefreshReceipts(message);
                return false;
            }

            MarkReachable(peer);
            _store.SetReceipt(messageId, peer.Id, 1);
            RefreshReceipts(message);
            Save(contact, message);
            // Пока сообщение передавалось, его успели изменить — этому участнику правка нужна отдельно.
            if (message.Text != packet.Text && contact.Messages.Contains(message))
            {
                message.IsEdited = true;
                Save(contact, message);
                EnqueueEdit(peer.Id, message, group.Id);
            }
        }
        return true;
    }

    /// <summary>Обновляет у своего сообщения в группе, скольким оно доставлено и кто прочитал.</summary>
    private void RefreshReceipts(ChatMessage message)
    {
        if (!message.IsOutgoing) return;
        var (total, delivered, read) = _store.ReceiptCounts(message.Id);
        if (total == 0) return;
        message.RecipientCount = total;
        message.DeliveredCount = delivered;
        message.ReadCount = read;
        message.Status = read > 0 ? MessageStatus.Read : delivered > 0 ? MessageStatus.Delivered : MessageStatus.Queued;
    }

    /// <summary>Отмечаем входящие в группе прочитанными и сообщаем об этом каждому автору.</summary>
    private void MarkGroupRead(Contact contact)
    {
        var unread = contact.Messages.Where(m => !m.IsOutgoing && !m.IsRead).ToList();
        foreach (var message in unread)
        {
            message.IsRead = true;
            // Отметку сразу кладём в очередь — флаг «отправлена» нужен только личным перепискам.
            message.ReadReceiptSent = true;
            Save(contact, message);
        }
        if (contact.UnreadCount != 0)
        {
            contact.UnreadCount = 0;
            UnreadChanged?.Invoke();
        }

        foreach (var bySender in unread.Where(m => m.SenderId != null && !m.IsSystem).GroupBy(m => m.SenderId!.Value))
        {
            EnqueuePacket(bySender.Key, new ChatPacket
            {
                Type = ChatPacket.ReadReceipt,
                Id = Guid.NewGuid(),
                GroupId = contact.Key,
                MessageIds = bySender.Select(m => m.Id).ToList(),
            });
        }
    }

    private void OnGroupReadReceipt(Contact contact, Guid readerId, List<Guid> messageIds)
    {
        foreach (var id in messageIds)
            _store.SetReceipt(id, readerId, 2);
        var ids = messageIds.ToHashSet();
        foreach (var message in contact.Messages.Where(m => m.IsOutgoing && ids.Contains(m.Id)))
            RefreshReceipts(message);
    }

    // ---- «печатает…» ----

    private void NotifyGroupTyping(Contact contact)
    {
        foreach (var member in contact.Group!.Members.Where(m => m.Id != MyId))
        {
            if (_discovery.Find(member.Id) is not { IsOnline: true } peer) continue;
            var packet = new ChatPacket { Type = ChatPacket.Typing, Id = Guid.NewGuid(), GroupId = contact.Key };
            Stamp(packet);
            _ = _messaging.SendAsync(peer.Address, peer.Port, packet);
        }
    }

    // ---- Входящие ----

    /// <summary>
    /// Переписка группы, к которой относится пакет участника. Если группы у нас ещё нет, а это сообщение
    /// (оно обогнало пакет «вас добавили»), — создаём её по составу из пакета. null — пакет не нужен:
    /// мы из группы вышли или нас в ней нет.
    /// </summary>
    private Contact? GroupForPacket(ChatPacket packet, Contact sender)
    {
        if (_groups.TryGetValue(packet.GroupId, out var contact))
        {
            NoteMember(contact, packet.From, sender.Title);
            return contact;
        }
        if (_store.HasLeftGroup(packet.GroupId)) return null;
        if (packet.Type is not (ChatPacket.Message or ChatPacket.Image or ChatPacket.File) ||
            packet.GroupMembers?.Any(m => m.Id == MyId) != true)
            return null;

        contact = CreateGroupFromPacket(packet, sender);
        AddServiceRecord(contact, $"Вы в группе «{contact.Title}»");
        return contact;
    }

    private void OnGroupUpdate(Contact sender, ChatPacket packet)
    {
        var who = sender.Title;
        var members = packet.GroupMembers ?? new List<GroupMember>();
        if (!_groups.TryGetValue(packet.GroupId, out var contact))
        {
            // Группы у нас нет: создаём, только если нас в неё позвали. Из группы мы выходили —
            // вернёмся, только если нас добавили заново (старые события этой группы не в счёт).
            if (packet.GroupAction == ChatPacket.GroupLeft || members.All(m => m.Id != MyId) ||
                (_store.HasLeftGroup(packet.GroupId) && packet.GroupAction != ChatPacket.GroupMembersAdded))
                return;
            contact = CreateGroupFromPacket(packet, sender);
            var others = contact.Group!.Members.Where(m => m.Id != MyId && m.Id != packet.From).ToList();
            AddServiceRecord(contact, packet.GroupAction == ChatPacket.GroupCreated
                ? $"{who} создал(а) группу «{contact.Title}»" + (others.Count > 0 ? $". Участники: вы, {Names(others)}" : " с вами")
                : $"{who} добавил(а) вас в группу «{contact.Title}»", notify: true);
            return;
        }

        var group = contact.Group!;
        switch (packet.GroupAction)
        {
            case ChatPacket.GroupCreated:
            case ChatPacket.GroupMembersAdded:
                var added = members.Where(m => !group.HasMember(m.Id)).ToList();
                if (added.Count == 0) break;
                group.Members.AddRange(added);
                AddServiceRecord(contact, $"{who} добавил(а): {Names(added)}");
                break;
            case ChatPacket.GroupRenamed:
                var name = CleanGroupName(packet.GroupName);
                if (name == group.Name) break;
                group.Name = name;
                AddServiceRecord(contact, $"{who} переименовал(а) группу в «{name}»");
                break;
            case ChatPacket.GroupLeft:
                if (group.Members.RemoveAll(m => m.Id == packet.From) > 0)
                    AddServiceRecord(contact, $"{who} вышел(-ла) из группы");
                break;
        }
        if (packet.GroupAction != ChatPacket.GroupLeft)
            NoteMember(contact, packet.From, who);
        _store.SaveGroup(group);
        UpdateGroupStatus(contact);
    }

    private Contact CreateGroupFromPacket(ChatPacket packet, Contact sender)
    {
        var members = (packet.GroupMembers ?? new List<GroupMember>()).DistinctBy(m => m.Id).ToList();
        if (members.All(m => m.Id != packet.From))
            members.Add(new GroupMember(packet.From, sender.Title));
        var group = new ChatGroup { Id = packet.GroupId, Name = CleanGroupName(packet.GroupName), Members = members };
        _store.ForgetLeftGroup(group.Id);
        _store.SaveGroup(group);
        Log.Info($"Нас добавили в группу «{group.Name}» ({members.Count} участников)");
        return AddGroupContact(group);
    }

    /// <summary>
    /// Автор пакета — участник группы: добавляем, если о его добавлении мы ещё не узнали,
    /// и обновляем имя, если он его сменил.
    /// </summary>
    private void NoteMember(Contact contact, Guid id, string name)
    {
        var group = contact.Group!;
        var index = group.Members.FindIndex(m => m.Id == id);
        if (index >= 0 && (group.Members[index].Name == name || name.Length == 0)) return;
        if (index >= 0) group.Members[index] = new GroupMember(id, name);
        else group.Members.Add(new GroupMember(id, name));
        _store.SaveGroup(group);
        UpdateGroupStatus(contact);
    }

    // ---- Служебное ----

    /// <summary>Запись о событии группы в её переписке (видна только у себя).</summary>
    private void AddServiceRecord(Contact contact, string text, bool notify = false)
    {
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            IsOutgoing = false,
            Text = text,
            Timestamp = DateTime.Now,
            Kind = MessageKind.Service,
            Status = MessageStatus.Delivered,
            IsRead = true,
            ReadReceiptSent = true,
        };
        contact.Messages.Add(message);
        Save(contact, message);
        // «Вас добавили в группу» — показываем всплывающим окном, иначе новую группу легко не заметить.
        if (notify && !IsConversationVisible(contact) && !contact.IsMuted)
            MessageReceived?.Invoke(contact, message);
    }

    private void UpdateGroupStatuses()
    {
        foreach (var contact in _groups.Values)
            UpdateGroupStatus(contact);
    }

    private void UpdateGroupStatus(Contact contact)
    {
        var group = contact.Group!;
        var count = group.Members.Count;
        var online = group.Members.Count(m => m.Id != MyId && IsPeerOnline(m.Id));
        group.StatusText = $"{count} {Plural(count, "участник", "участника", "участников")} · " +
                           (online == 0 ? "никого нет в сети" : $"в сети {online}");
    }

    private static string CleanGroupName(string name)
    {
        name = string.Join(" ", name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length > MaxGroupNameLength) name = name[..MaxGroupNameLength];
        return name.Length > 0 ? name : "Группа";
    }

    private static string Names(IEnumerable<GroupMember> members) => string.Join(", ", members.Select(m => m.Name));

    private static string Plural(int n, string one, string few, string many)
    {
        var mod100 = n % 100;
        var mod10 = n % 10;
        if (mod100 is >= 11 and <= 14) return many;
        return mod10 switch { 1 => one, >= 2 and <= 4 => few, _ => many };
    }
}
