using System.Collections.ObjectModel;
using System.Net;
using System.Windows.Threading;
using OfficeChat.Models;

namespace OfficeChat.Services;

/// <summary>
/// Логика чата поверх сети: список контактов, очередь неотправленных сообщений,
/// статусы «доставлено/прочитано», повторные попытки. Работает в UI-потоке.
/// </summary>
public sealed class ChatService : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly AppSettings _settings;
    private readonly DiscoveryService _discovery;
    private readonly MessagingService _messaging;
    private readonly Dictionary<Guid, Contact> _contactsByPeer = new();
    private readonly HashSet<Guid> _receivedMessageIds = new();
    private readonly HashSet<Guid> _flushing = new();
    private readonly DispatcherTimer _retryTimer;

    /// <summary>«Все» первым, затем компьютеры по алфавиту.</summary>
    public ObservableCollection<Contact> Contacts { get; } = new();

    public Contact Everyone { get; } = Contact.Everyone();

    /// <summary>Пришло новое входящее сообщение.</summary>
    public event Action<Contact, ChatMessage>? MessageReceived;

    /// <summary>Кто-то появился в сети или ушёл.</summary>
    public event Action? PresenceChanged;

    /// <summary>
    /// Открыта ли сейчас переписка на экране (тогда входящие сразу считаются прочитанными).
    /// Задаёт окно.
    /// </summary>
    public Func<Contact, bool> IsConversationVisible { get; set; } = _ => false;

    public int OnlineCount => _contactsByPeer.Values.Count(c => c.IsOnline);

    public ChatService(AppSettings settings)
    {
        _settings = settings;
        Contacts.Add(Everyone);

        _discovery = new DiscoveryService(settings);
        _discovery.PeerOnline += OnPeerOnline;
        _discovery.PeerOffline += OnPeerOffline;

        _messaging = new MessagingService();
        _messaging.PacketReceived += OnPacketReceived;

        _retryTimer = new DispatcherTimer { Interval = RetryInterval };
        _retryTimer.Tick += (_, _) => FlushAll();
    }

    public void Start()
    {
        _messaging.Start();
        _discovery.Start();
        _retryTimer.Start();
    }

    /// <summary>Сразу разослать актуальные данные о себе (после смены имени).</summary>
    public void AnnounceNow() => _discovery.AnnounceNow();

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

    /// <summary>Отменяет отправку сообщения, которое ещё ждёт в очереди.</summary>
    public bool Cancel(ChatMessage message)
    {
        if (!message.CanCancel) return false;
        var contact = _contactsByPeer.Values.FirstOrDefault(c => c.Messages.Contains(message));
        if (contact == null) return false;

        contact.Messages.Remove(message);
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
            changed = true;
        }
        contact.UnreadCount = 0;

        if (changed)
            _ = FlushAsync(contact);
    }

    private void Enqueue(Contact contact, string text, bool isBroadcast)
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
        _ = FlushAsync(contact);
    }

    // ---- Отправка очереди ----

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
        if (!peer.IsOnline || !_flushing.Add(peer.Id)) return;
        try
        {
            while (contact.Messages.FirstOrDefault(m => m.IsOutgoing && m.Status == MessageStatus.Queued)
                   is { } message)
            {
                message.Status = MessageStatus.Sending;
                var delivered = await _messaging.SendAsync(peer.Address, new ChatPacket
                {
                    Type = ChatPacket.Message,
                    Id = message.Id,
                    From = _settings.UserId,
                    FromName = _settings.DisplayName,
                    FromMachine = Environment.MachineName,
                    Text = message.Text,
                    SentAt = new DateTimeOffset(message.Timestamp),
                    IsBroadcast = message.IsBroadcast,
                });

                if (!delivered)
                {
                    message.PeerOffline = !peer.IsOnline;
                    message.Status = MessageStatus.Queued;
                    return;
                }
                // Отметка «прочитано» могла прийти раньше, чем мы обработали подтверждение.
                if (message.Status == MessageStatus.Sending)
                    message.Status = MessageStatus.Delivered;
            }

            var readMessages = contact.Messages
                .Where(m => !m.IsOutgoing && m.IsRead && !m.ReadReceiptSent)
                .ToList();
            if (readMessages.Count > 0)
            {
                var sent = await _messaging.SendAsync(peer.Address, new ChatPacket
                {
                    Type = ChatPacket.ReadReceipt,
                    Id = Guid.NewGuid(),
                    From = _settings.UserId,
                    FromName = _settings.DisplayName,
                    FromMachine = Environment.MachineName,
                    MessageIds = readMessages.Select(m => m.Id).ToList(),
                });
                if (sent)
                    readMessages.ForEach(m => m.ReadReceiptSent = true);
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
        _discovery.Observe(packet.From, packet.FromName, packet.FromMachine, from);
        var contact = GetOrAddContact(_discovery.Find(packet.From)!);

        switch (packet.Type)
        {
            case ChatPacket.Message:
                ReceiveMessage(contact, packet);
                break;
            case ChatPacket.ReadReceipt when packet.MessageIds != null:
                var ids = packet.MessageIds.ToHashSet();
                foreach (var message in contact.Messages.Where(m => m.IsOutgoing && ids.Contains(m.Id)))
                    message.Status = MessageStatus.Read;
                break;
        }
    }

    private void ReceiveMessage(Contact contact, ChatPacket packet)
    {
        // Повтор, если до отправителя не дошло наше подтверждение.
        if (!_receivedMessageIds.Add(packet.Id)) return;

        var message = new ChatMessage
        {
            Id = packet.Id,
            IsOutgoing = false,
            Text = packet.Text,
            Timestamp = packet.SentAt.LocalDateTime,
            IsBroadcast = packet.IsBroadcast,
        };
        contact.Messages.Add(message);

        if (IsConversationVisible(contact))
            MarkRead(contact);
        else
            contact.UnreadCount++;

        MessageReceived?.Invoke(contact, message);
    }

    // ---- Контакты ----

    private void OnPeerOnline(Peer peer)
    {
        var contact = GetOrAddContact(peer);
        foreach (var message in contact.Messages.Where(m => m.CanCancel))
            message.PeerOffline = false;
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
        if (contact.IsOnline || contact.Messages.Count > 0) return;
        _contactsByPeer.Remove(contact.Peer!.Id);
        Contacts.Remove(contact);
    }

    public void Dispose()
    {
        _retryTimer.Stop();
        _discovery.Dispose();
        _messaging.Dispose();
    }
}
