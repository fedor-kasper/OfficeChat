using System.IO;
using System.Collections.ObjectModel;
using System.Net;
using System.Windows.Threading;
using OfficeChat.Models;

namespace OfficeChat.Services;

/// <summary>
/// Логика чата поверх сети: список контактов, очередь неотправленных сообщений,
/// статусы «доставлено/прочитано», повторные попытки, история. Работает в UI-потоке.
/// </summary>
public sealed class ChatService : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    /// <summary>Сколько последних сообщений показывать сразу (и подгружать по кнопке).</summary>
    private const int PageSize = 200;

    private readonly AppSettings _settings;
    private readonly HistoryStore _store;
    private readonly DiscoveryService _discovery;
    private readonly MessagingService _messaging;
    private readonly Dictionary<Guid, Contact> _contactsByPeer = new();
    private readonly HashSet<Guid> _flushing = new();
    // Собеседники, которых нельзя убирать из списка (например, с ними идёт игра).
    private readonly HashSet<Guid> _pinnedPeers = new();
    private readonly DispatcherTimer _retryTimer;

    /// <summary>«Все» первым, затем компьютеры по алфавиту.</summary>
    public ObservableCollection<Contact> Contacts { get; } = new();

    public Contact Everyone { get; } = Contact.Everyone();

    /// <summary>Пришло новое входящее сообщение.</summary>
    public event Action<Contact, ChatMessage>? MessageReceived;

    /// <summary>Изменилось общее число непрочитанных.</summary>
    public event Action? UnreadChanged;

    /// <summary>Кто-то появился в сети или ушёл.</summary>
    public event Action? PresenceChanged;

    /// <summary>Пришёл пакет мини-игры (обрабатывает <see cref="GameService"/>).</summary>
    public event Action<Contact, ChatPacket>? GamePacketReceived;

    /// <summary>
    /// Открыта ли сейчас переписка на экране (тогда входящие сразу считаются прочитанными).
    /// Задаёт окно.
    /// </summary>
    public Func<Contact, bool> IsConversationVisible { get; set; } = _ => false;

    public int TotalUnread => _contactsByPeer.Values.Sum(c => c.UnreadCount);

    /// <summary>TCP-порт, на котором принимаем сообщения.</summary>
    public int MessagingPort => _messaging.Port;

    public int OnlineCount => _contactsByPeer.Values.Count(c => c.IsOnline);

    public ChatService(AppSettings settings)
    {
        _settings = settings;
        _store = new HistoryStore();
        Contacts.Add(Everyone);

        _discovery = new DiscoveryService(settings);
        _discovery.PeerOnline += OnPeerOnline;
        _discovery.PeerOffline += OnPeerOffline;

        _messaging = new MessagingService();
        _messaging.PacketReceived += OnPacketReceived;

        _retryTimer = new DispatcherTimer { Interval = RetryInterval };
        _retryTimer.Tick += (_, _) => FlushAll();

        LoadHistory();
    }

    public void Start()
    {
        _messaging.Start();
        // Порт сообщений может оказаться нестандартным (второй пользователь Windows) — сообщаем его остальным.
        _discovery.MessagingPort = _messaging.Port;
        _discovery.Start();
        _retryTimer.Start();
    }

    /// <summary>Сразу разослать актуальные данные о себе (после смены имени).</summary>
    public void AnnounceNow() => _discovery.AnnounceNow();

    // ---- История ----

    private void LoadHistory()
    {
        foreach (var stored in _store.LoadContacts())
        {
            var peer = _discovery.AddKnown(stored.Id, stored.Name, stored.Machine, stored.Address);
            var contact = GetOrAddContact(peer);

            // Последние сообщения + всё, с чем ещё есть работа (очередь, непрочитанные), даже если оно старее.
            var recent = _store.LoadRecent(peer.Id, PageSize);
            var pending = _store.LoadPending(peer.Id);
            var messages = recent.Concat(pending.Where(p => recent.All(r => r.Id != p.Id)))
                .OrderBy(m => m.Timestamp)
                .ToList();

            foreach (var message in messages)
            {
                if (message.CanCancel) message.PeerOffline = true;
                contact.Messages.Add(message);
            }
            contact.HasOlderMessages = recent.Count == PageSize;
            contact.UnreadCount = messages.Count(m => !m.IsOutgoing && !m.IsRead);
            RemoveIfForgotten(contact);
        }
    }

    /// <summary>Подгружает в окно следующую порцию более ранних сообщений.</summary>
    public void LoadOlder(Contact contact)
    {
        if (contact.IsEveryone || contact.Messages.Count == 0) return;

        var older = _store.LoadRecent(contact.Peer!.Id, PageSize, contact.Messages[0].Timestamp);
        var loaded = contact.Messages.Select(m => m.Id).ToHashSet();
        var index = 0;
        foreach (var message in older.Where(m => !loaded.Contains(m.Id)))
            contact.Messages.Insert(index++, message);
        contact.HasOlderMessages = older.Count == PageSize;
    }

    /// <summary>Удаляет всю переписку с собеседником (вместе с неотправленными сообщениями).</summary>
    public void ClearConversation(Contact contact)
    {
        if (contact.IsEveryone) return;

        _store.DeleteConversation(contact.Peer!.Id);
        contact.Messages.Clear();
        contact.HasOlderMessages = false;
        if (contact.UnreadCount != 0)
        {
            contact.UnreadCount = 0;
            UnreadChanged?.Invoke();
        }
        RemoveIfForgotten(contact);
    }

    /// <summary>
    /// Убирает собеседника из списка вместе с историей. Только для тех, кто не в сети:
    /// тот, кто в сети, тут же появился бы снова.
    /// </summary>
    public bool RemoveContact(Contact contact)
    {
        if (contact.IsEveryone || contact.IsOnline) return false;

        var peerId = contact.Peer!.Id;
        _store.DeleteContact(peerId);
        contact.Messages.Clear();
        _contactsByPeer.Remove(peerId);
        Contacts.Remove(contact);
        _discovery.Forget(peerId);
        if (contact.UnreadCount != 0)
            UnreadChanged?.Invoke();
        return true;
    }

    // ---- Для мини-игр ----

    /// <summary>
    /// Отправляет служебный пакет собеседнику. false — не дошло.
    /// Пробуем по последнему известному адресу, даже если обнаружение считает собеседника
    /// ушедшим: UDP-рассылку может блокировать брандмауэр, а прямое TCP-соединение — работать.
    /// </summary>
    public async Task<bool> SendPacketAsync(Contact contact, ChatPacket packet)
    {
        var peer = contact.Peer;
        if (peer == null || peer.Address.Equals(IPAddress.None)) return false;

        packet.From = _settings.UserId;
        packet.FromName = _settings.DisplayName;
        packet.FromMachine = Environment.MachineName;
        packet.FromPort = _messaging.Port;
        var delivered = await _messaging.SendAsync(peer.Address, peer.Port, packet);
        if (delivered) MarkReachable(peer);
        return delivered;
    }

    /// <summary>Собеседник принял наш пакет — значит, он точно в сети.</summary>
    private void MarkReachable(Peer peer) =>
        _discovery.Observe(peer.Id, peer.Name, peer.Machine, peer.Address);

    /// <summary>Актуальный контакт собеседника по его идентификатору.</summary>
    public Contact? FindContact(Guid peerId) => _contactsByPeer.GetValueOrDefault(peerId);

    /// <summary>Не убирать собеседника из списка, даже если он ушёл из сети без переписки.</summary>
    public void PinContact(Contact contact)
    {
        if (contact.Peer != null) _pinnedPeers.Add(contact.Peer.Id);
    }

    public void UnpinContact(Contact contact)
    {
        if (contact.Peer == null || !_pinnedPeers.Remove(contact.Peer.Id)) return;
        RemoveIfForgotten(contact);
    }

    /// <summary>Добавляет в историю переписки запись об игре (видна только у себя).</summary>
    public void AddGameRecord(Contact contact, string text)
    {
        if (contact.IsEveryone) return;

        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            IsOutgoing = false,
            Text = text,
            Timestamp = DateTime.Now,
            Kind = MessageKind.Game,
            Status = MessageStatus.Delivered,
            // Служебная запись не считается непрочитанной и не требует отметки «прочитано».
            IsRead = true,
            ReadReceiptSent = true,
        };
        contact.Messages.Add(message);
        _store.SaveContact(contact.Peer!);
        Save(contact, message);
    }

    private void Save(Contact contact, ChatMessage message)
    {
        // Сообщение могли удалить (очистка переписки), пока шла отправка, — не воскрешаем его.
        if (contact.Messages.Contains(message))
            _store.SaveMessage(contact.Peer!.Id, message);
    }

    // ---- Отправка ----

    /// <summary>
    /// Отправляет текст контакту. Для «Все» — каждому, кто в сети, отдельным личным сообщением.
    /// Возвращает число получателей.
    /// </summary>
    public int Send(Contact contact, string text)
    {
        if (!contact.IsEveryone)
        {
            Enqueue(contact, text, isBroadcast: false);
            return 1;
        }

        var recipients = _contactsByPeer.Values.Where(c => c.IsOnline).ToList();
        foreach (var recipient in recipients)
            Enqueue(recipient, text, isBroadcast: true);
        return recipients.Count;
    }

    /// <summary>Личный ответ собеседнику (например, из всплывающего окна). Возвращает созданное сообщение.</summary>
    public ChatMessage Reply(Contact contact, string text)
    {
        var message = Enqueue(contact, text, isBroadcast: false);
        // Раз ответил — значит, прочитал.
        MarkRead(contact);
        return message;
    }

    /// <summary>
    /// Отправляет изображение (с необязательной подписью). Для «Все» — каждому, кто в сети,
    /// отдельной копией. Возвращает число получателей.
    /// </summary>
    public int SendImage(Contact contact, byte[] data, string fileName, string caption)
    {
        var recipients = contact.IsEveryone
            ? _contactsByPeer.Values.Where(c => c.IsOnline).ToList()
            : new List<Contact> { contact };
        foreach (var recipient in recipients)
            EnqueueImage(recipient, data, fileName, caption, contact.IsEveryone);
        Log.Info($"Изображение «{fileName}» ({data.Length / 1024} КБ) поставлено в очередь для {recipients.Count} получателей");
        return recipients.Count;
    }

    private void EnqueueImage(Contact contact, byte[] data, string fileName, string caption, bool isBroadcast)
    {
        var id = Guid.NewGuid();
        // У каждой копии свой файл: удаление одной переписки не трогает другие.
        var path = ImageStore.Save(id, fileName, data);
        var message = new ChatMessage
        {
            Id = id,
            IsOutgoing = true,
            Kind = MessageKind.Image,
            Text = caption,
            FileName = fileName,
            ImagePath = path,
            Timestamp = DateTime.Now,
            IsBroadcast = isBroadcast,
            Status = MessageStatus.Queued,
            PeerOffline = !contact.IsOnline,
        };
        contact.Messages.Add(message);
        _store.SaveContact(contact.Peer!);
        Save(contact, message);
        _ = FlushAsync(contact);
    }

    /// <summary>Отменяет отправку сообщения, которое ещё ждёт в очереди.</summary>
    public bool Cancel(ChatMessage message)
    {
        if (!message.CanCancel) return false;
        var contact = _contactsByPeer.Values.FirstOrDefault(c => c.Messages.Contains(message));
        if (contact == null) return false;

        contact.Messages.Remove(message);
        _store.DeleteMessage(message.Id);
        if (message.IsImage) ImageStore.Delete(message.ImagePath);
        RemoveIfForgotten(contact);
        return true;
    }

    /// <summary>Отмечает все входящие в переписке прочитанными и сообщает об этом отправителю.</summary>
    public void MarkRead(Contact contact)
    {
        if (contact.IsEveryone) return;

        var changed = false;
        foreach (var message in contact.Messages.Where(m => !m.IsOutgoing && !m.IsRead))
        {
            message.IsRead = true;
            Save(contact, message);
            changed = true;
        }
        if (contact.UnreadCount != 0)
        {
            contact.UnreadCount = 0;
            UnreadChanged?.Invoke();
        }

        if (changed)
            _ = FlushAsync(contact);
    }

    private ChatMessage Enqueue(Contact contact, string text, bool isBroadcast)
    {
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(),
            IsOutgoing = true,
            Text = text,
            Timestamp = DateTime.Now,
            IsBroadcast = isBroadcast,
            Status = MessageStatus.Queued,
            PeerOffline = !contact.IsOnline,
        };
        contact.Messages.Add(message);
        _store.SaveContact(contact.Peer!);
        Save(contact, message);
        _ = FlushAsync(contact);
        return message;
    }

    private void FlushAll()
    {
        foreach (var contact in _contactsByPeer.Values.ToList())
            _ = FlushAsync(contact);
    }

    /// <summary>
    /// Отправляет по порядку всё, что ждёт в очереди, затем отметки «прочитано».
    /// При первой ошибке останавливается, чтобы не нарушить порядок сообщений.
    /// </summary>
    private async Task FlushAsync(Contact contact)
    {
        var peer = contact.Peer!;
        var hasWork = contact.Messages.Any(m =>
            (m.IsOutgoing && m.Status == MessageStatus.Queued) ||
            (!m.IsOutgoing && m.IsRead && !m.ReadReceiptSent));
        if (!hasWork || peer.Address.Equals(IPAddress.None) || !_flushing.Add(peer.Id)) return;
        // Даже если обнаружение считает собеседника ушедшим, пробуем напрямую по последнему адресу:
        // UDP-рассылку может резать брандмауэр, а TCP при этом работать.
        try
        {
            while (contact.Messages.FirstOrDefault(m => m.IsOutgoing && m.Status == MessageStatus.Queued)
                   is { } message)
            {
                var packet = new ChatPacket
                {
                    Type = message.IsImage ? ChatPacket.Image : ChatPacket.Message,
                    Id = message.Id,
                    From = _settings.UserId,
                    FromName = _settings.DisplayName,
                    FromMachine = Environment.MachineName,
                    FromPort = _messaging.Port,
                    Text = message.Text,
                    SentAt = new DateTimeOffset(message.Timestamp),
                    IsBroadcast = message.IsBroadcast,
                    FileName = message.FileName,
                };
                if (message.IsImage)
                {
                    // Байты читаем с диска при каждой попытке — очередь переживает перезапуск программы.
                    try
                    {
                        packet.Payload = await File.ReadAllBytesAsync(message.ImagePath);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Log.Error($"Файл изображения {message.ImagePath} недоступен — убираем сообщение из очереди", ex);
                        contact.Messages.Remove(message);
                        _store.DeleteMessage(message.Id);
                        continue;
                    }
                }

                message.Status = MessageStatus.Sending;
                var delivered = await _messaging.SendAsync(peer.Address, peer.Port, packet);

                if (!delivered)
                {
                    message.PeerOffline = !peer.IsOnline;
                    message.FailureHint = _messaging.LastFailureFor(peer.Address, peer.Port) switch
                    {
                        SendFailure.NoAnswer => "Компьютер получателя не отвечает: вероятно, его брандмауэр блокирует OfficeChat",
                        SendFailure.Refused => "OfficeChat у получателя не принимает подключения",
                        _ => "",
                    };
                    message.Status = MessageStatus.Queued;
                    return;
                }
                message.FailureHint = "";
                MarkReachable(peer);
                // Отметка «прочитано» могла прийти раньше, чем мы обработали подтверждение.
                if (message.Status == MessageStatus.Sending)
                    message.Status = MessageStatus.Delivered;
                Save(contact, message);
            }

            var readMessages = contact.Messages
                .Where(m => !m.IsOutgoing && m.IsRead && !m.ReadReceiptSent)
                .ToList();
            if (readMessages.Count > 0)
            {
                var sent = await _messaging.SendAsync(peer.Address, peer.Port, new ChatPacket
                {
                    Type = ChatPacket.ReadReceipt,
                    Id = Guid.NewGuid(),
                    From = _settings.UserId,
                    FromName = _settings.DisplayName,
                    FromMachine = Environment.MachineName,
                    FromPort = _messaging.Port,
                    MessageIds = readMessages.Select(m => m.Id).ToList(),
                });
                if (sent)
                {
                    foreach (var message in readMessages)
                    {
                        message.ReadReceiptSent = true;
                        Save(contact, message);
                    }
                }
            }
        }
        finally
        {
            _flushing.Remove(peer.Id);
        }
    }

    // ---- Входящие ----

    private void OnPacketReceived(ChatPacket packet, IPAddress from)
    {
        if (packet.From == _settings.UserId) return;

        // Раз прислал пакет — значит, точно в сети, даже если обнаружение до нас ещё не дошло.
        _discovery.Observe(packet.From, packet.FromName, packet.FromMachine, from, packet.FromPort);
        var contact = GetOrAddContact(_discovery.Find(packet.From)!);

        switch (packet.Type)
        {
            case ChatPacket.Message:
            case ChatPacket.Image:
                ReceiveMessage(contact, packet);
                break;
            case not null when packet.Type.StartsWith("game-", StringComparison.Ordinal):
                GamePacketReceived?.Invoke(contact, packet);
                break;
            case ChatPacket.ReadReceipt when packet.MessageIds != null:
                var ids = packet.MessageIds.ToHashSet();
                foreach (var message in contact.Messages.Where(m => m.IsOutgoing && ids.Contains(m.Id)))
                    message.Status = MessageStatus.Read;
                _store.MarkOutgoingRead(contact.Peer!.Id, ids);
                break;
        }
    }

    private void ReceiveMessage(Contact contact, ChatPacket packet)
    {
        // Повтор, если до отправителя не дошло наше подтверждение.
        if (_store.HasMessage(packet.Id)) return;

        var isImage = packet.Type == ChatPacket.Image;
        var imagePath = "";
        if (isImage)
        {
            if (packet.Payload is not { Length: > 0 }) return;
            try
            {
                imagePath = ImageStore.Save(packet.Id, packet.FileName, packet.Payload);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Не удалось сохранить изображение от «{contact.Title}»", ex);
                return;
            }
        }

        var message = new ChatMessage
        {
            Id = packet.Id,
            IsOutgoing = false,
            Kind = isImage ? MessageKind.Image : MessageKind.Text,
            Text = packet.Text,
            FileName = packet.FileName,
            ImagePath = imagePath,
            Timestamp = packet.SentAt.LocalDateTime,
            IsBroadcast = packet.IsBroadcast,
        };
        contact.Messages.Add(message);
        _store.SaveContact(contact.Peer!);
        Save(contact, message);

        if (IsConversationVisible(contact))
            MarkRead(contact);
        else
        {
            contact.UnreadCount++;
            UnreadChanged?.Invoke();
        }

        MessageReceived?.Invoke(contact, message);
    }

    // ---- Контакты ----

    private void OnPeerOnline(Peer peer)
    {
        var contact = GetOrAddContact(peer);
        foreach (var message in contact.Messages.Where(m => m.CanCancel))
            message.PeerOffline = false;
        // Обновляем имя и адрес у тех, с кем уже есть переписка.
        if (contact.Messages.Count > 0)
            _store.SaveContact(peer);
        PresenceChanged?.Invoke();
        _ = FlushAsync(contact);
    }

    private void OnPeerOffline(Peer peer)
    {
        if (!_contactsByPeer.TryGetValue(peer.Id, out var contact)) return;
        foreach (var message in contact.Messages.Where(m => m.CanCancel))
            message.PeerOffline = true;
        RemoveIfForgotten(contact);
        PresenceChanged?.Invoke();
    }

    private Contact GetOrAddContact(Peer peer)
    {
        if (_contactsByPeer.TryGetValue(peer.Id, out var contact))
            return contact;

        contact = Contact.For(peer);
        _contactsByPeer.Add(peer.Id, contact);

        var index = 1;
        while (index < Contacts.Count &&
               string.Compare(Contacts[index].Title, contact.Title, StringComparison.CurrentCultureIgnoreCase) < 0)
            index++;
        Contacts.Insert(index, contact);
        return contact;
    }

    /// <summary>Ушедший из сети компьютер без переписки убираем из списка, с перепиской — оставляем серым.</summary>
    private void RemoveIfForgotten(Contact contact)
    {
        if (contact.IsOnline || contact.Messages.Count > 0 || _pinnedPeers.Contains(contact.Peer!.Id)) return;
        _contactsByPeer.Remove(contact.Peer!.Id);
        Contacts.Remove(contact);
        _store.DeleteContact(contact.Peer.Id);
        _discovery.Forget(contact.Peer.Id);
    }

    public void Dispose()
    {
        _retryTimer.Stop();
        _discovery.Dispose();
        _messaging.Dispose();
        _store.Dispose();
    }
}
