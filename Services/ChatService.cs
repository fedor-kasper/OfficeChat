using System.IO;
using System.Collections.ObjectModel;
using System.Net;
using System.Text.Json;
using System.Windows.Threading;
using OfficeChat.Models;

namespace OfficeChat.Services;

/// <summary>
/// Логика чата поверх сети: список контактов, очередь неотправленных сообщений,
/// статусы «доставлено/прочитано», повторные попытки, история. Работает в UI-потоке.
/// </summary>
public sealed partial class ChatService : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    // «печатает…»: отправляем не чаще раза в несколько секунд, показываем чуть дольше этого интервала.
    private static readonly TimeSpan TypingSendInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TypingShowTime = TimeSpan.FromSeconds(6);

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
    private readonly DispatcherTimer _typingTimer;
    // Когда последний раз сообщали собеседнику, что мы печатаем.
    private readonly Dictionary<Guid, DateTime> _typingSent = new();
    // Служебные пакеты (правки, удаления), которые ещё не дошли до собеседников. Копия — в базе.
    private readonly List<OutboxItem> _outbox;

    /// <summary>Как подписан автор цитаты, если ответили на ваше сообщение.</summary>
    public const string MyReplyAuthor = "Вы";

    /// <summary>«Все» первым, затем группы и компьютеры по алфавиту.</summary>
    public ObservableCollection<Contact> Contacts { get; } = new();

    public Contact Everyone { get; } = Contact.Everyone();

    /// <summary>Пришло новое входящее сообщение.</summary>
    public event Action<Contact, ChatMessage>? MessageReceived;

    /// <summary>Изменилось число непрочитанных (или у переписки включили/выключили уведомления).</summary>
    public event Action? UnreadChanged;

    /// <summary>Кто-то появился в сети или ушёл.</summary>
    public event Action? PresenceChanged;

    /// <summary>Сообщение убрано из переписки (удалено у себя или собеседником) — убрать его и из всплывающих окон.</summary>
    public event Action<Contact, ChatMessage>? MessageRemoved;

    /// <summary>Пришёл пакет обновления программы (обрабатывает <see cref="UpdateService"/>).</summary>
    public event Action<Contact, ChatPacket>? UpdatePacketReceived;

    /// <summary>Пришёл пакет мини-игры (обрабатывает <see cref="GameService"/>).</summary>
    public event Action<Contact, ChatPacket>? GamePacketReceived;

    /// <summary>
    /// Открыта ли сейчас переписка на экране (тогда входящие сразу считаются прочитанными).
    /// Задаёт окно.
    /// </summary>
    public Func<Contact, bool> IsConversationVisible { get; set; } = _ => false;

    /// <summary>Непрочитанные в переписках с включёнными уведомлениями — их показывает значок в трее.</summary>
    public int TotalUnread => Conversations.Where(c => !c.IsMuted).Sum(c => c.UnreadCount);

    /// <summary>Все переписки: личные и групповые.</summary>
    private IEnumerable<Contact> Conversations => _contactsByPeer.Values.Concat(_groups.Values);

    /// <summary>Наш Id в сети.</summary>
    public Guid MyId => _settings.UserId;

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

        _typingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _typingTimer.Tick += (_, _) => ExpireTyping();

        _outbox = _store.LoadOutbox();
        LoadHistory();
    }

    public void Start()
    {
        _messaging.Start();
        // Порт сообщений может оказаться нестандартным (второй пользователь Windows) — сообщаем его остальным.
        _discovery.MessagingPort = _messaging.Port;
        _discovery.Start();
        _retryTimer.Start();
        _typingTimer.Start();
    }

    /// <summary>Наша версия и система — для пакетов обнаружения (по ним коллеги находят обновление).</summary>
    public void SetUpdateInfo(string version, string platform, bool canShare)
    {
        _discovery.AppVersion = version;
        _discovery.Platform = platform;
        _discovery.CanShareUpdate = canShare;
        _discovery.AnnounceNow();
    }

    /// <summary>Сейчас передаётся вложение — перезапускаться для обновления не время.</summary>
    public bool HasTransfersInProgress =>
        Conversations.Any(c => c.Messages.Any(m => m.IsOutgoing && m.Status == MessageStatus.Sending));

    /// <summary>Сразу разослать актуальные данные о себе (после смены имени).</summary>
    public void AnnounceNow() => _discovery.AnnounceNow();

    // ---- Уведомления ----

    /// <summary>Включает или выключает всплывающие окна для переписки (запоминается в настройках).</summary>
    public void SetMuted(Contact contact, bool muted)
    {
        if (contact.IsEveryone || contact.IsMuted == muted) return;
        contact.IsMuted = muted;
        var id = contact.Key;
        _settings.MutedChats.Remove(id);
        if (muted) _settings.MutedChats.Add(id);
        SettingsService.Save(_settings);
        UnreadChanged?.Invoke();
    }

    // ---- «печатает…» ----

    /// <summary>
    /// Пользователь набирает текст в переписке — сообщаем собеседнику (не чаще раза в несколько секунд).
    /// Не доходит — не страшно: это не сообщение, повторять не нужно.
    /// </summary>
    public void NotifyTyping(Contact contact)
    {
        if (contact.IsEveryone || !contact.IsOnline) return;
        var id = contact.Key;
        var now = DateTime.UtcNow;
        if (_typingSent.TryGetValue(id, out var last) && now - last < TypingSendInterval) return;
        _typingSent[id] = now;
        if (contact.IsGroup)
            NotifyGroupTyping(contact);
        else
            _ = SendPacketAsync(contact, new ChatPacket { Type = ChatPacket.Typing, Id = Guid.NewGuid() });
    }

    /// <summary>Сообщение ушло — следующее нажатие клавиши снова сообщит «печатает…».</summary>
    private void ResetTypingSent(Contact contact) => _typingSent.Remove(contact.Key);

    private void ShowTyping(Contact contact, string text)
    {
        contact.TypingUntil = DateTime.UtcNow + TypingShowTime;
        contact.TypingText = text;
    }

    private static void StopTyping(Contact contact) => contact.TypingText = "";

    private void ExpireTyping()
    {
        var now = DateTime.UtcNow;
        foreach (var contact in Conversations.Where(c => c.IsTyping && c.TypingUntil < now))
            StopTyping(contact);
    }

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
        LoadGroups();
    }

    /// <summary>Последние сообщения переписки и всё, с чем ещё есть работа (очередь, непрочитанные).</summary>
    private void LoadMessages(Contact contact)
    {
        var recent = _store.LoadRecent(contact.Key, PageSize);
        var pending = _store.LoadPending(contact.Key);
        var messages = recent.Concat(pending.Where(p => recent.All(r => r.Id != p.Id)))
            .OrderBy(m => m.Timestamp)
            .ToList();
        foreach (var message in messages)
        {
            if (contact.IsGroup) RefreshReceipts(message);
            contact.Messages.Add(message);
        }
        contact.HasOlderMessages = recent.Count == PageSize;
        contact.UnreadCount = messages.Count(m => !m.IsOutgoing && !m.IsRead);
    }

    /// <summary>Подгружает в окно следующую порцию более ранних сообщений.</summary>
    public void LoadOlder(Contact contact)
    {
        if (contact.IsEveryone || contact.Messages.Count == 0) return;

        var older = _store.LoadRecent(contact.Key, PageSize, contact.Messages[0].Timestamp);
        var loaded = contact.Messages.Select(m => m.Id).ToHashSet();
        var index = 0;
        foreach (var message in older.Where(m => !loaded.Contains(m.Id)))
        {
            if (contact.IsGroup) RefreshReceipts(message);
            contact.Messages.Insert(index++, message);
        }
        contact.HasOlderMessages = older.Count == PageSize;
    }

    /// <summary>Удаляет всю переписку с собеседником (вместе с неотправленными сообщениями).</summary>
    public void ClearConversation(Contact contact)
    {
        if (contact.IsEveryone) return;

        _store.DeleteConversation(contact.Key);
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

        Stamp(packet);
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
            _store.SaveMessage(contact.Key, message);
    }

    /// <summary>
    /// Добавляет своё новое сообщение в переписку, сохраняет и запускает отправку:
    /// собеседнику — из очереди переписки, в группе — каждому участнику отдельно.
    /// </summary>
    private void Post(Contact contact, ChatMessage message)
    {
        contact.Messages.Add(message);
        ResetTypingSent(contact);
        if (contact.IsGroup)
        {
            PostToGroup(contact, message);
            return;
        }
        _store.SaveContact(contact.Peer!);
        Save(contact, message);
        _ = FlushAsync(contact);
    }

    // ---- Отправка ----

    /// <summary>
    /// Отправляет текст контакту. Для «Все» — каждому, кто в сети, отдельным личным сообщением.
    /// Возвращает число получателей.
    /// </summary>
    public int Send(Contact contact, string text, ChatMessage? replyTo = null)
    {
        if (!contact.IsEveryone)
        {
            Enqueue(contact, text, isBroadcast: false, replyTo);
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
    public int SendImage(Contact contact, byte[] data, string fileName, string caption, ChatMessage? replyTo = null)
    {
        var recipients = contact.IsEveryone
            ? _contactsByPeer.Values.Where(c => c.IsOnline).ToList()
            : new List<Contact> { contact };
        foreach (var recipient in recipients)
            EnqueueImage(recipient, data, fileName, caption, contact.IsEveryone, replyTo);
        Log.Info($"Изображение «{fileName}» ({data.Length / 1024} КБ) поставлено в очередь для {recipients.Count} получателей");
        return recipients.Count;
    }

    private void EnqueueImage(Contact contact, byte[] data, string fileName, string caption, bool isBroadcast,
        ChatMessage? replyTo)
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
            ReplyToId = replyTo?.Id,
            ReplyAuthor = replyTo == null ? "" : ReplyAuthorOf(contact, replyTo),
            ReplyText = replyTo?.QuoteText ?? "",
        };
        Post(contact, message);
    }

    /// <summary>
    /// Отправляет файл любого типа (с необязательной подписью). Файл копируется в хранилище программы
    /// (в фоне — он может быть большим), чтобы его можно было отправить позже и открыть из истории.
    /// Для «Все» — каждому, кто в сети; копия файла при этом одна на всех. Возвращает число получателей.
    /// </summary>
    public async Task<int> SendFileAsync(Contact contact, string sourcePath, string caption, ChatMessage? replyTo = null)
    {
        var recipients = contact.IsEveryone
            ? _contactsByPeer.Values.Where(c => c.IsOnline).ToList()
            : new List<Contact> { contact };
        if (recipients.Count == 0) return 0;

        var fileName = Path.GetFileName(sourcePath);
        var storedPath = FileStore.NewPath(Guid.NewGuid(), fileName);
        await Task.Run(() => File.Copy(sourcePath, storedPath, overwrite: true));
        var size = new FileInfo(storedPath).Length;

        foreach (var recipient in recipients)
        {
            var message = new ChatMessage
            {
                Id = Guid.NewGuid(),
                IsOutgoing = true,
                Kind = MessageKind.File,
                Text = caption,
                FileName = fileName,
                FilePath = storedPath,
                FileSize = size,
                Timestamp = DateTime.Now,
                IsBroadcast = contact.IsEveryone,
                Status = MessageStatus.Queued,
                PeerOffline = !recipient.IsOnline,
                ReplyToId = replyTo?.Id,
                ReplyAuthor = replyTo == null ? "" : ReplyAuthorOf(recipient, replyTo),
                ReplyText = replyTo?.QuoteText ?? "",
            };
            Post(recipient, message);
        }
        Log.Info($"Файл «{fileName}» ({size / 1024} КБ) поставлен в очередь для {recipients.Count} получателей");
        return recipients.Count;
    }

    /// <summary>Удаляет файл вложения, если на него больше не ссылается ни одно сообщение.</summary>
    private void DeleteFileIfUnused(ChatMessage message)
    {
        if (message.IsFile && message.FilePath.Length > 0 && !_store.IsFileReferenced(message.FilePath))
            FileStore.Delete(message.FilePath);
    }

    /// <summary>Отменяет отправку сообщения, которое ещё ждёт в очереди.</summary>
    public bool Cancel(ChatMessage message)
    {
        if (!message.CanCancel) return false;
        var contact = Conversations.FirstOrDefault(c => c.Messages.Contains(message));
        if (contact == null) return false;

        RemoveMessage(contact, message);
        return true;
    }

    // ---- Правка и удаление ----

    /// <summary>
    /// Меняет текст своего сообщения (у вложения — подпись). Ещё не отправленное просто уйдёт с новым текстом,
    /// доставленное — получит правку у собеседника. Возвращает false, если менять нечего или нельзя.
    /// </summary>
    public bool Edit(Contact contact, ChatMessage message, string newText)
    {
        if (!message.CanEdit || contact.IsEveryone || !contact.Messages.Contains(message)) return false;
        if (message.Text == newText || (message.Kind == MessageKind.Text && newText.Length == 0)) return false;

        message.Text = newText;
        if (contact.IsGroup)
        {
            // Тем, кому сообщение ещё не ушло, оно уйдёт уже с новым текстом; остальным — правка.
            var deliveredTo = _store.DeliveredTo(message.Id);
            if (deliveredTo.Count > 0) message.IsEdited = true;
            Save(contact, message);
            foreach (var peerId in deliveredTo)
                EnqueueEdit(peerId, message, contact.Key);
            Log.Info($"Сообщение {message.Id} в группе изменено");
            return true;
        }
        // Пока сообщение не ушло, правка незаметна для собеседника — и пометка «изменено» не нужна.
        if (message.Status != MessageStatus.Queued) message.IsEdited = true;
        Save(contact, message);
        // Уходит прямо сейчас — правку отправим, когда станет известно, что оно дошло (см. FlushAsync).
        if (message.Status is MessageStatus.Delivered or MessageStatus.Read)
            EnqueueEdit(contact.Peer!.Id, message);
        Log.Info($"Сообщение {message.Id} изменено");
        return true;
    }

    private void EnqueueEdit(Guid peerId, ChatMessage message, Guid groupId = default) =>
        EnqueuePacket(peerId, new ChatPacket
        {
            Type = ChatPacket.Edit,
            Id = Guid.NewGuid(),
            TargetId = message.Id,
            Text = message.Text,
            GroupId = groupId,
        });

    /// <summary>
    /// Удаляет сообщение у себя; <paramref name="forEveryone"/> — и у собеседника (только своё).
    /// Неотправленное просто отменяется.
    /// </summary>
    public void Delete(Contact contact, ChatMessage message, bool forEveryone)
    {
        if (contact.IsEveryone || !contact.Messages.Contains(message)) return;

        // Кому сообщение уже дошло — им и отправлять удаление (в группе считаем до того, как сотрём отметки доставки).
        var reachedPeers = !message.IsOutgoing ? new List<Guid>()
            : contact.IsGroup ? _store.DeliveredTo(message.Id)
            : message.Status != MessageStatus.Queued ? new List<Guid> { contact.Peer!.Id }
            : new List<Guid>();
        RemoveMessage(contact, message);
        if (forEveryone && message.CanDeleteForEveryone)
        {
            foreach (var peerId in reachedPeers)
            {
                EnqueuePacket(peerId, new ChatPacket
                {
                    Type = ChatPacket.Delete,
                    Id = Guid.NewGuid(),
                    TargetId = message.Id,
                    GroupId = contact.IsGroup ? contact.Key : Guid.Empty,
                });
            }
        }
        Log.Info($"Сообщение {message.Id} удалено {(forEveryone ? "у всех" : "у себя")}");
    }

    /// <summary>Убирает сообщение из переписки, базы и с диска (вложения).</summary>
    private void RemoveMessage(Contact contact, ChatMessage message)
    {
        contact.Messages.Remove(message);
        _store.DeleteMessage(message.Id);
        if (message.IsImage) ImageStore.Delete(message.ImagePath);
        DeleteFileIfUnused(message);
        if (!message.IsOutgoing && !message.IsRead && contact.UnreadCount > 0)
        {
            contact.UnreadCount--;
            UnreadChanged?.Invoke();
        }
        MessageRemoved?.Invoke(contact, message);
        RemoveIfForgotten(contact);
    }

    /// <summary>Собеседник изменил своё сообщение (в личной переписке или в группе).</summary>
    private void ApplyEdit(Contact contact, ChatPacket packet)
    {
        var message = FindIncoming(contact, packet.TargetId, packet.From);
        if (message == null) return;
        StopTyping(contact);
        message.Text = packet.Text;
        message.IsEdited = true;
        _store.SaveMessage(contact.Key, message);
    }

    /// <summary>Собеседник удалил своё сообщение у всех.</summary>
    private void ApplyDelete(Contact contact, ChatPacket packet)
    {
        var message = FindIncoming(contact, packet.TargetId, packet.From);
        if (message == null) return;
        if (contact.Messages.Contains(message))
        {
            RemoveMessage(contact, message);
            return;
        }
        // Старое сообщение, которое не загружено в окно, — удаляем из базы.
        _store.DeleteMessage(message.Id);
        if (message.IsImage) ImageStore.Delete(message.ImagePath);
        DeleteFileIfUnused(message);
    }

    /// <summary>
    /// Входящее сообщение от <paramref name="senderId"/> в этой переписке: загруженное в окно или из базы.
    /// Чужие и свои сообщения так не найти — править и удалять можно только то, что прислал сам автор.
    /// </summary>
    private ChatMessage? FindIncoming(Contact contact, Guid id, Guid senderId)
    {
        bool IsFromSender(ChatMessage m) => !m.IsOutgoing && !m.IsSystem && (!contact.IsGroup || m.SenderId == senderId);

        var loaded = contact.Messages.FirstOrDefault(m => m.Id == id);
        if (loaded != null) return IsFromSender(loaded) ? loaded : null;
        return _store.LoadMessage(id) is { } stored && stored.PeerId == contact.Key && IsFromSender(stored.Message)
            ? stored.Message
            : null;
    }

    // ---- Очередь служебных пакетов ----

    /// <summary>Ставит служебный пакет в очередь собеседнику; уйдёт по порядку, когда он будет в сети.</summary>
    private void EnqueuePacket(Guid peerId, ChatPacket packet)
    {
        _outbox.Add(_store.AddOutbox(peerId, JsonSerializer.Serialize(packet)));
        _ = FlushAsync(peerId);
    }

    /// <summary>Отмечает все входящие в переписке прочитанными и сообщает об этом отправителю.</summary>
    public void MarkRead(Contact contact)
    {
        if (contact.IsEveryone) return;
        if (contact.IsGroup)
        {
            MarkGroupRead(contact);
            return;
        }

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

    private ChatMessage Enqueue(Contact contact, string text, bool isBroadcast, ChatMessage? replyTo = null)
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
            ReplyToId = replyTo?.Id,
            ReplyAuthor = replyTo == null ? "" : ReplyAuthorOf(contact, replyTo),
            ReplyText = replyTo?.QuoteText ?? "",
        };
        Post(contact, message);
        return message;
    }

    /// <summary>Как подписать автора цитаты у себя: «Вы», автор сообщения в группе или имя собеседника.</summary>
    private static string ReplyAuthorOf(Contact contact, ChatMessage original) =>
        original.IsOutgoing ? MyReplyAuthor : original.SenderName.Length > 0 ? original.SenderName : contact.Title;

    private void FlushAll()
    {
        foreach (var peerId in _contactsByPeer.Keys
                     .Concat(_outbox.Select(o => o.PeerId))
                     .Concat(_store.PeersWithPendingGroupDeliveries())
                     .Distinct().ToList())
            _ = FlushAsync(peerId);
    }

    private Task FlushAsync(Contact contact) => FlushAsync(contact.Peer!.Id);

    /// <summary>
    /// Отправляет по порядку всё, что ждёт в очереди, затем отметки «прочитано».
    /// При первой ошибке останавливается, чтобы не нарушить порядок сообщений.
    /// </summary>
    private async Task FlushAsync(Guid peerId)
    {
        var contact = _contactsByPeer.GetValueOrDefault(peerId);
        var peer = contact?.Peer ?? _discovery.Find(peerId);
        if (peer == null) return;
        var hasWork = _outbox.Any(o => o.PeerId == peerId) || (contact != null && contact.Messages.Any(m =>
            (m.IsOutgoing && m.Status == MessageStatus.Queued) ||
            (!m.IsOutgoing && m.IsRead && !m.ReadReceiptSent))) || _store.PendingGroupDeliveries(peerId).Count > 0;
        if (!hasWork || peer.Address.Equals(IPAddress.None) || !_flushing.Add(peer.Id)) return;
        // Даже если обнаружение считает собеседника ушедшим, пробуем напрямую по последнему адресу:
        // UDP-рассылку может резать брандмауэр, а TCP при этом работать.
        try
        {
            if (contact != null && !await FlushMessagesAsync(contact, peer)) return;
            // Служебные пакеты (в том числе «вас добавили в группу») — раньше сообщений групп.
            if (!await FlushOutboxAsync(peer)) return;
            await FlushGroupMessagesAsync(peer);
        }
        finally
        {
            _flushing.Remove(peer.Id);
        }
    }

    /// <summary>
    /// Служебные пакеты собеседнику — строго по порядку, при ошибке ждём следующей попытки.
    /// false — не всё ушло.
    /// </summary>
    private async Task<bool> FlushOutboxAsync(Peer peer)
    {
        while (_outbox.FirstOrDefault(o => o.PeerId == peer.Id) is { } item)
        {
            var packet = JsonSerializer.Deserialize<ChatPacket>(item.PacketJson)!;
            Stamp(packet);
            if (!await _messaging.SendAsync(peer.Address, peer.Port, packet)) return false;
            MarkReachable(peer);
            _outbox.Remove(item);
            _store.DeleteOutbox(item.Seq);
        }
        return true;
    }

    /// <summary>Подписывает пакет нашими данными: кто отправил и куда отвечать.</summary>
    private void Stamp(ChatPacket packet)
    {
        packet.From = _settings.UserId;
        packet.FromName = _settings.DisplayName;
        packet.FromMachine = Environment.MachineName;
        packet.FromPort = _messaging.Port;
    }

    /// <summary>
    /// Личные сообщения из очереди, затем отметки «прочитано». false — не всё ушло (собеседник недоступен).
    /// </summary>
    private async Task<bool> FlushMessagesAsync(Contact contact, Peer peer)
    {
        while (contact.Messages.FirstOrDefault(m => m.IsOutgoing && m.Status == MessageStatus.Queued)
               is { } message)
        {
            var packet = BuildMessagePacket(message);
            if (packet == null)
            {
                contact.Messages.Remove(message);
                _store.DeleteMessage(message.Id);
                continue;
            }

            message.Status = MessageStatus.Sending;
            message.TransferProgress = 0;
            var delivered = await _messaging.SendAsync(peer.Address, peer.Port, packet,
                packet.PayloadLength > 0 ? new Progress<double>(p => message.TransferProgress = p) : null);
            message.TransferProgress = 0;

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
                return false;
            }
            message.FailureHint = "";
            MarkReachable(peer);
            // Отметка «прочитано» могла прийти раньше, чем мы обработали подтверждение.
            if (message.Status == MessageStatus.Sending)
                message.Status = MessageStatus.Delivered;
            Save(contact, message);
            // Пока сообщение передавалось, его успели изменить — отправляем правку следом.
            if (message.Text != packet.Text && contact.Messages.Contains(message))
            {
                message.IsEdited = true;
                Save(contact, message);
                EnqueueEdit(peer.Id, message);
            }
        }

        var readMessages = contact.Messages
            .Where(m => !m.IsOutgoing && m.IsRead && !m.ReadReceiptSent)
            .ToList();
        if (readMessages.Count > 0)
        {
            var receipt = new ChatPacket
            {
                Type = ChatPacket.ReadReceipt,
                Id = Guid.NewGuid(),
                MessageIds = readMessages.Select(m => m.Id).ToList(),
            };
            Stamp(receipt);
            var sent = await _messaging.SendAsync(peer.Address, peer.Port, receipt);
            if (sent)
            {
                foreach (var message in readMessages)
                {
                    message.ReadReceiptSent = true;
                    Save(contact, message);
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Пакет своего сообщения (текст, изображение или файл). null — файл вложения пропал с диска,
    /// отправлять нечего.
    /// </summary>
    private ChatPacket? BuildMessagePacket(ChatMessage message)
    {
        var packet = new ChatPacket
        {
            Type = ChatPacket.Message,
            Id = message.Id,
            Text = message.Text,
            SentAt = new DateTimeOffset(message.Timestamp),
            IsBroadcast = message.IsBroadcast,
            FileName = message.FileName,
            ReplyToId = message.ReplyToId,
            // У себя автор цитаты — «Вы», а собеседнику нужно наше имя.
            ReplyAuthor = message.ReplyAuthor == MyReplyAuthor ? _settings.DisplayName : message.ReplyAuthor,
            ReplyText = message.ReplyText,
        };
        Stamp(packet);
        if (message.IsImage || message.IsFile)
        {
            // Вложение уходит потоком прямо с диска — очередь переживает перезапуск, а большой файл
            // не загружается в память целиком.
            var attachment = message.IsImage ? message.ImagePath : message.FilePath;
            if (!File.Exists(attachment))
            {
                Log.Error($"Файл вложения {attachment} пропал — убираем сообщение из очереди");
                return null;
            }
            packet.Type = message.IsImage ? ChatPacket.Image : ChatPacket.File;
            packet.PayloadPath = attachment;
        }
        return packet;
    }

    // ---- Входящие ----

    private void OnPacketReceived(ChatPacket packet, IPAddress from)
    {
        if (packet.From == _settings.UserId) return;

        // Раз прислал пакет — значит, точно в сети, даже если обнаружение до нас ещё не дошло.
        _discovery.Observe(packet.From, packet.FromName, packet.FromMachine, from, packet.FromPort);
        var contact = GetOrAddContact(_discovery.Find(packet.From)!);

        // Вложение пришло к пакету, которому оно не положено (чужая версия) — не оставляем мусор.
        if (packet.ReceivedPayloadPath != null &&
            packet.Type is not (ChatPacket.Image or ChatPacket.File or ChatPacket.UpdatePackage))
            FileStore.DeleteQuietly(packet.ReceivedPayloadPath);

        // Пакет относится к группе — дальше работаем с перепиской группы, а не с личной.
        Contact conversation = contact;
        if (packet.IsGroup && packet.Type != ChatPacket.GroupUpdate)
        {
            var group = GroupForPacket(packet, contact);
            if (group == null)
            {
                if (packet.ReceivedPayloadPath != null) FileStore.DeleteQuietly(packet.ReceivedPayloadPath);
                return;
            }
            conversation = group;
        }

        switch (packet.Type)
        {
            case ChatPacket.Message:
            case ChatPacket.Image:
            case ChatPacket.File:
                ReceiveMessage(conversation, packet);
                break;
            case ChatPacket.Typing:
                ShowTyping(conversation, conversation.IsGroup ? $"{contact.Title} печатает…" : "печатает…");
                break;
            case ChatPacket.Edit:
                ApplyEdit(conversation, packet);
                break;
            case ChatPacket.Delete:
                ApplyDelete(conversation, packet);
                break;
            case ChatPacket.UpdateRequest:
            case ChatPacket.UpdatePackage:
                UpdatePacketReceived?.Invoke(contact, packet);
                break;
            case ChatPacket.GroupUpdate when packet.IsGroup:
                OnGroupUpdate(contact, packet);
                break;
            case ChatPacket.ReadReceipt when packet.MessageIds != null && conversation.IsGroup:
                OnGroupReadReceipt(conversation, contact.Peer!.Id, packet.MessageIds);
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

    /// <summary>Входящее сообщение в личную переписку или в группу (<paramref name="contact"/> — куда положить).</summary>
    private void ReceiveMessage(Contact contact, ChatPacket packet)
    {
        var received = packet.ReceivedPayloadPath;

        // Повтор, если до отправителя не дошло наше подтверждение.
        if (_store.HasMessage(packet.Id))
        {
            if (received != null) FileStore.DeleteQuietly(received);
            return;
        }

        var kind = packet.Type switch
        {
            ChatPacket.Image => MessageKind.Image,
            ChatPacket.File => MessageKind.File,
            _ => MessageKind.Text,
        };
        var imagePath = "";
        var filePath = "";
        long fileSize = 0;
        if (kind != MessageKind.Text)
        {
            if (received == null) return;
            try
            {
                // Принятое вложение уже на диске во временном файле — переносим его на место.
                if (kind == MessageKind.Image)
                {
                    Directory.CreateDirectory(ImageStore.Folder);
                    imagePath = ImageStore.PathFor(packet.Id, packet.FileName);
                    File.Move(received, imagePath, overwrite: true);
                }
                else
                {
                    filePath = FileStore.NewPath(packet.Id, packet.FileName);
                    File.Move(received, filePath, overwrite: true);
                    fileSize = new FileInfo(filePath).Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error($"Не удалось сохранить вложение «{packet.FileName}» от «{contact.Title}»", ex);
                FileStore.DeleteQuietly(received);
                return;
            }
        }

        // Сообщение пришло — значит, собеседник уже не печатает.
        StopTyping(contact);

        var message = new ChatMessage
        {
            Id = packet.Id,
            IsOutgoing = false,
            Kind = kind,
            Text = packet.Text,
            FileName = FileStore.SafeFileName(packet.FileName),
            ImagePath = imagePath,
            FilePath = filePath,
            FileSize = fileSize,
            Timestamp = packet.SentAt.LocalDateTime,
            IsBroadcast = packet.IsBroadcast,
            ReplyToId = packet.ReplyToId,
            // Ответили на наше сообщение — подписываем цитату «Вы», а не нашим именем.
            ReplyAuthor = packet.ReplyToId is { } replyTo && IsMyMessage(contact, replyTo) ? MyReplyAuthor : packet.ReplyAuthor,
            ReplyText = packet.ReplyText.Length > ChatMessage.MaxQuoteLength + 1
                ? packet.ReplyText[..(ChatMessage.MaxQuoteLength + 1)]
                : packet.ReplyText,
            SenderId = contact.IsGroup ? packet.From : null,
            SenderName = contact.IsGroup ? packet.FromName : "",
        };
        contact.Messages.Add(message);
        if (contact.Peer != null) _store.SaveContact(contact.Peer);
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

    private bool IsMyMessage(Contact contact, Guid id) =>
        contact.Messages.FirstOrDefault(m => m.Id == id) is { } loaded
            ? loaded.IsOutgoing
            : _store.LoadMessage(id) is { } stored && stored.Message.IsOutgoing;

    // ---- Контакты ----

    private void OnPeerOnline(Peer peer)
    {
        var contact = GetOrAddContact(peer);
        foreach (var message in contact.Messages.Where(m => m.CanCancel))
            message.PeerOffline = false;
        // Обновляем имя и адрес у тех, с кем уже есть переписка.
        if (contact.Messages.Count > 0)
            _store.SaveContact(peer);
        UpdateGroupStatuses();
        PresenceChanged?.Invoke();
        _ = FlushAsync(contact);
    }

    private void OnPeerOffline(Peer peer)
    {
        if (!_contactsByPeer.TryGetValue(peer.Id, out var contact)) return;
        foreach (var message in contact.Messages.Where(m => m.CanCancel))
            message.PeerOffline = true;
        StopTyping(contact);
        RemoveIfForgotten(contact);
        UpdateGroupStatuses();
        PresenceChanged?.Invoke();
    }

    private Contact GetOrAddContact(Peer peer)
    {
        if (_contactsByPeer.TryGetValue(peer.Id, out var contact))
            return contact;

        contact = Contact.For(peer);
        contact.IsMuted = _settings.MutedChats.Contains(peer.Id);
        _contactsByPeer.Add(peer.Id, contact);

        InsertSorted(contact);
        return contact;
    }

    /// <summary>Вставляет в список: после «Все» — группы, затем люди; внутри — по алфавиту.</summary>
    private void InsertSorted(Contact contact)
    {
        var index = 1;
        while (index < Contacts.Count &&
               (Contacts[index].IsGroup && !contact.IsGroup ||
                Contacts[index].IsGroup == contact.IsGroup &&
                string.Compare(Contacts[index].Title, contact.Title, StringComparison.CurrentCultureIgnoreCase) < 0))
            index++;
        Contacts.Insert(index, contact);
    }

    /// <summary>Ушедший из сети компьютер без переписки убираем из списка, с перепиской — оставляем серым.</summary>
    private void RemoveIfForgotten(Contact contact)
    {
        if (contact.IsGroup || contact.IsOnline || contact.Messages.Count > 0 || _pinnedPeers.Contains(contact.Peer!.Id)) return;
        _contactsByPeer.Remove(contact.Peer!.Id);
        Contacts.Remove(contact);
        _store.DeleteContact(contact.Peer.Id);
        _discovery.Forget(contact.Peer.Id);
    }

    public void Dispose()
    {
        _retryTimer.Stop();
        _typingTimer.Stop();
        _discovery.Dispose();
        _messaging.Dispose();
        _store.Dispose();
    }
}
