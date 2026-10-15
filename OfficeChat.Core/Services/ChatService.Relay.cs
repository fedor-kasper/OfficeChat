using System.IO;
using System.Net;
using System.Text.Json;
using OfficeChat.Models;

namespace OfficeChat.Services;

/// <summary>
/// Пересылка за других. Сообщение (текст или изображение), его правка и удаление, напоминание и отметки по нему
/// для того, кто сейчас не в сети, раздаются копиями всем, кто в сети. Они хранят копию и передают её
/// получателю, когда тот появится, — даже если автор к тому времени выключил компьютер.
/// Копии передаются и тем, кто включится позже. Получатель отбрасывает повторы (по Id),
/// а о полученном сообщении сообщает автору («доставлено» — тоже через коллег, если автора нет в сети).
/// </summary>
public sealed partial class ChatService
{
    private static readonly TimeSpan RelayLifetime = TimeSpan.FromDays(30);
    /// <summary>Больше этого через коллег не передаём (файлы и большие изображения ждут встречи с адресатом).</summary>
    private const long RelayMaxPayload = 5 * 1024 * 1024;

    private static string RelayFolder => Path.Combine(SettingsService.DataFolder, "relay");

    // Кому и какие копии уже отдали в этом сеансе — чтобы не слать одно и то же при каждой встрече.
    private readonly HashSet<(Guid Packet, Guid Peer)> _relayHanded = new();

    /// <summary>Служебные пакеты из очереди, которые можно передавать через других.</summary>
    private static bool IsRelayableService(string? type) =>
        type is ChatPacket.ReminderShare or ChatPacket.ReminderDelete or ChatPacket.ReminderDone
            or ChatPacket.Edit or ChatPacket.Delete or ChatPacket.Delivered;

    private static bool IsRelayable(string? type) =>
        IsRelayableService(type) || type is ChatPacket.Message or ChatPacket.Image;

    private bool IsOnline(Guid peerId) => _discovery.Find(peerId) is { IsOnline: true };

    /// <summary>
    /// Раздать копии всего, что ждёт тех, кто не в сети: служебных пакетов из очереди и своих сообщений
    /// (личных и в группах). Всем, кто в сети, или одному <paramref name="onlyTo"/>.
    /// </summary>
    private void OfferRelays(Peer? onlyTo = null)
    {
        foreach (var item in _outbox.ToList())
        {
            if (item.PeerId == onlyTo?.Id || IsOnline(item.PeerId)) continue;
            ChatPacket? packet;
            try
            {
                packet = JsonSerializer.Deserialize<ChatPacket>(item.PacketJson);
            }
            catch (JsonException)
            {
                continue;
            }
            if (packet == null || !IsRelayableService(packet.Type)) continue;
            // Подписываем копию сразу: передавать её будут другие, а автор — мы.
            Stamp(packet);
            HandOver(item.PeerId, packet, null, onlyTo);
        }

        // Личные сообщения в очереди к тем, кто не в сети.
        foreach (var contact in _contactsByPeer.Values.Where(c => !c.IsOnline && c.Peer!.Id != onlyTo?.Id).ToList())
        {
            foreach (var message in contact.Messages.Where(m => m.IsOutgoing && m.Status == MessageStatus.Queued).ToList())
            {
                if (!CanRelay(message) || BuildMessagePacket(message) is not { } packet) continue;
                HandOver(contact.Peer!.Id, packet, message, onlyTo);
            }
        }

        // Сообщения в группах участникам, которые не в сети.
        foreach (var peerId in _store.PeersWithPendingGroupDeliveries())
        {
            if (peerId == onlyTo?.Id || IsOnline(peerId)) continue;
            foreach (var (messageId, groupId) in _store.PendingGroupDeliveries(peerId))
            {
                if (!_groups.TryGetValue(groupId, out var group)) continue;
                var message = group.Messages.FirstOrDefault(m => m.Id == messageId) ?? _store.LoadMessage(messageId)?.Message;
                if (message == null || !CanRelay(message) || BuildGroupPacket(group, message) is not { } packet) continue;
                HandOver(peerId, packet, message, onlyTo);
            }
        }
    }

    /// <summary>Через коллег идут текст и небольшие изображения (стикеры, скриншоты); файлы — нет.</summary>
    private static bool CanRelay(ChatMessage message) =>
        message.Kind == MessageKind.Text ||
        (message.IsImage && File.Exists(message.ImagePath) && new FileInfo(message.ImagePath).Length <= RelayMaxPayload);

    /// <summary>Отдать копию пакета для <paramref name="targetId"/> тем, кто в сети (или одному <paramref name="onlyTo"/>).</summary>
    private void HandOver(Guid targetId, ChatPacket packet, ChatMessage? message, Peer? onlyTo)
    {
        // Адресату доставит сервер.
        if (ViaServer(targetId)) return;
        var holders = onlyTo != null
            ? new List<Peer> { onlyTo }
            : _contactsByPeer.Values.Where(c => c.IsOnline).Select(c => c.Peer!).ToList();
        var json = JsonSerializer.Serialize(packet);
        foreach (var holder in holders)
        {
            if (holder.Id == targetId || holder.Id == packet.From || holder.Id == MyId) continue;
            if (!_relayHanded.Add((packet.Id, holder.Id))) continue;
            _ = SendRelayCopyAsync(holder, targetId, packet.Id, json, packet.PayloadPath, message);
        }
    }

    private async Task SendRelayCopyAsync(Peer holder, Guid targetId, Guid packetId, string packetJson,
        string? payloadPath, ChatMessage? message)
    {
        var packet = new ChatPacket { Type = ChatPacket.Relay, Id = packetId, TargetId = targetId, Text = packetJson };
        if (payloadPath != null)
        {
            if (!File.Exists(payloadPath)) return;
            packet.PayloadPath = payloadPath;
        }
        Stamp(packet);
        if (!await _messaging.SendAsync(holder.Address, holder.Port, packet))
        {
            _relayHanded.Remove((packetId, holder.Id)); // не дошло — попробуем при следующей встрече
            return;
        }
        MarkReachable(holder);
        if (message != null)
        {
            _store.MarkHandedToRelays(message.Id);
            message.IsRelayed = true;
        }
    }

    /// <summary>Кто-то появился в сети: отдать ему копии своего и хранимого для тех, кто всё ещё не в сети.</summary>
    private void OfferRelaysTo(Peer peer)
    {
        OfferRelays(peer);
        foreach (var item in _store.PendingRelays())
        {
            if (item.TargetId == peer.Id || item.OriginId == peer.Id || IsOnline(item.TargetId)) continue;
            if (!_relayHanded.Add((item.Id, peer.Id))) continue;
            _ = SendHeldCopyAsync(peer, item);
        }
    }

    private async Task SendHeldCopyAsync(Peer holder, RelayItem item)
    {
        var packet = new ChatPacket { Type = ChatPacket.Relay, Id = item.Id, TargetId = item.TargetId, Text = item.PacketJson };
        if (item.PayloadPath != null)
        {
            if (!File.Exists(item.PayloadPath)) return;
            packet.PayloadPath = item.PayloadPath;
        }
        Stamp(packet);
        if (await _messaging.SendAsync(holder.Address, holder.Port, packet))
            MarkReachable(holder);
        else
            _relayHanded.Remove((item.Id, holder.Id));
    }

    /// <summary>Пришла копия: для нас — обрабатываем как пакет от автора, для другого — храним до его появления.</summary>
    private void OnRelay(ChatPacket packet)
    {
        var payload = packet.ReceivedPayloadPath;
        ChatPacket? inner = null;
        try
        {
            inner = JsonSerializer.Deserialize<ChatPacket>(packet.Text);
        }
        catch (JsonException)
        {
        }
        if (inner == null || !IsRelayable(inner.Type) || inner.From == MyId || inner.From == Guid.Empty ||
            (inner.Type == ChatPacket.Image) != (payload != null))
        {
            if (payload != null) FileStore.DeleteQuietly(payload);
            return;
        }

        if (packet.TargetId == MyId)
        {
            Log.Info($"Получен «{inner.Type}» от «{inner.FromName}», переданный через «{packet.FromName}»");
            inner.ReceivedPayloadPath = payload;
            DeliverRelayed(inner);
            return;
        }

        // Храним для другого. Своё вложение держим отдельно от истории — до передачи адресату.
        string? stored = null;
        if (payload != null)
        {
            if (new FileInfo(payload).Length > RelayMaxPayload)
            {
                FileStore.DeleteQuietly(payload);
                return;
            }
            try
            {
                Directory.CreateDirectory(RelayFolder);
                stored = Path.Combine(RelayFolder, inner.Id.ToString("N"));
                File.Move(payload, stored, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Не удалось сохранить вложение для пересылки", ex);
                FileStore.DeleteQuietly(payload);
                return;
            }
        }
        if (!_store.AddRelay(new RelayItem(inner.Id, packet.TargetId, inner.From, packet.Text, stored)))
        {
            // Уже храним (или уже передали) — второй экземпляр не нужен.
            if (stored != null && _store.PendingRelays(packet.TargetId).All(r => r.PayloadPath != stored))
                FileStore.DeleteQuietly(stored);
            return;
        }
        Log.Info($"Взяли на хранение «{inner.Type}» от «{inner.FromName}» для {packet.TargetId}");
        _relayHanded.Add((inner.Id, packet.From)); // тому, кто отдал, копию возвращать незачем
        if (IsOnline(packet.TargetId))
            _ = FlushAsync(packet.TargetId);
    }

    /// <summary>Пакет для нас, переданный коллегой: обрабатываем от имени автора.</summary>
    private void DeliverRelayed(ChatPacket packet)
    {
        // Автора мы можем ещё не знать (не встречались в сети) — заводим его, адрес узнаем при встрече.
        var origin = _discovery.Find(packet.From) ??
                     _discovery.AddKnown(packet.From, packet.FromName, packet.FromMachine, IPAddress.None);
        var contact = GetOrAddContact(origin);
        var isNewMessage = (packet.Type is ChatPacket.Message or ChatPacket.Image) && !_store.HasMessage(packet.Id);
        HandlePacket(contact, packet);

        // Автору — «доставлено» (тоже через коллег, если его нет в сети), чтобы он не ждал встречи с нами.
        if (isNewMessage && _store.HasMessage(packet.Id))
        {
            EnqueuePacket(origin.Id, new ChatPacket
            {
                Type = ChatPacket.Delivered,
                Id = Guid.NewGuid(),
                MessageIds = new List<Guid> { packet.Id },
                GroupId = packet.GroupId,
            });
        }
    }

    /// <summary>Получатель сообщил, что наше сообщение дошло через коллег.</summary>
    private void OnDeliveredNotice(Contact sender, Contact conversation, List<Guid> ids)
    {
        foreach (var id in ids)
        {
            if (conversation.IsGroup)
            {
                _store.SetReceipt(id, sender.Peer!.Id, 1);
                if (conversation.Messages.FirstOrDefault(m => m.Id == id) is { } groupMessage)
                {
                    RefreshReceipts(groupMessage);
                    Save(conversation, groupMessage);
                }
            }
            else if (conversation.Messages.FirstOrDefault(m => m.Id == id && m.IsOutgoing) is
                     { Status: MessageStatus.Queued or MessageStatus.Sending } message)
            {
                message.Status = MessageStatus.Delivered;
                Save(conversation, message);
            }
        }
    }

    /// <summary>Передать получателю всё, что храним для него. false — не всё ушло.</summary>
    private async Task<bool> FlushRelaysAsync(Peer peer)
    {
        foreach (var item in _store.PendingRelays(peer.Id))
        {
            var packet = new ChatPacket { Type = ChatPacket.Relay, Id = item.Id, TargetId = peer.Id, Text = item.PacketJson };
            if (item.PayloadPath != null)
            {
                if (!File.Exists(item.PayloadPath))
                {
                    _store.MarkRelayDelivered(item.Id); // вложение пропало — передавать нечего
                    continue;
                }
                packet.PayloadPath = item.PayloadPath;
            }
            Stamp(packet);
            if (!await _messaging.SendAsync(peer.Address, peer.Port, packet)) return false;
            MarkReachable(peer);
            _store.MarkRelayDelivered(item.Id);
            if (item.PayloadPath != null) FileStore.DeleteQuietly(item.PayloadPath);
            Log.Info($"Передали за другого пакет {item.Id} получателю «{peer.Name}»");
        }
        return true;
    }

    /// <summary>Кому ещё не дошло своё сообщение (до удаления — потом отметки доставки стираются).</summary>
    private List<Guid> PendingTargetsOf(Contact contact, ChatMessage message) =>
        !message.IsOutgoing ? new List<Guid>()
        : contact.IsGroup ? _store.PendingRecipients(message.Id)
        : message.Status == MessageStatus.Queued ? new List<Guid> { contact.Peer!.Id }
        : new List<Guid>();

    /// <summary>
    /// Отменили сообщение, копии которого уже у коллег: догоняем их удалением. Если удаление придёт
    /// раньше самого сообщения, получатель его уже не покажет.
    /// </summary>
    private void RecallRelayed(Contact contact, ChatMessage message, List<Guid> pendingTargets)
    {
        if (pendingTargets.Count == 0 || !_store.WasHandedToRelays(message.Id)) return;
        foreach (var peerId in pendingTargets)
            EnqueuePacket(peerId, new ChatPacket
            {
                Type = ChatPacket.Delete,
                Id = Guid.NewGuid(),
                TargetId = message.Id,
                GroupId = contact.IsGroup ? contact.Key : Guid.Empty,
            });
    }

    /// <summary>Убрать вложения пересылки, которые больше не нужны (переданы или устарели).</summary>
    private void CleanRelayFolder()
    {
        try
        {
            if (!Directory.Exists(RelayFolder)) return;
            var needed = _store.PendingRelays().Select(r => r.PayloadPath).OfType<string>().ToHashSet();
            foreach (var file in Directory.GetFiles(RelayFolder).Where(f => !needed.Contains(f)))
                FileStore.DeleteQuietly(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Не удалось почистить папку пересылки", ex);
        }
    }

    /// <summary>Пакет напоминаний может прийти несколько раз (напрямую и через других) — обрабатываем один.</summary>
    private void DispatchReminderPacket(Guid from, ChatPacket packet)
    {
        if (packet.Id != Guid.Empty && !_store.MarkSeen(packet.Id)) return;
        ReminderPacketReceived?.Invoke(from, packet);
    }
}
