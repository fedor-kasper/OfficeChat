using System.Text.Json;
using OfficeChat.Models;

namespace OfficeChat.Services;

/// <summary>
/// Пересылка за других. Служебный пакет (напоминание, его удаление, отметка «выполнено») для того,
/// кто сейчас не в сети, раздаётся копиями всем, кто в сети. Они хранят копию и передают её
/// получателю, когда тот появится, — даже если автор к тому времени выключил компьютер.
/// Копии передаются и тем, кто включится позже. Получатель отбрасывает повторы (по Id пакета).
/// </summary>
public sealed partial class ChatService
{
    private static readonly TimeSpan RelayLifetime = TimeSpan.FromDays(30);

    // Кому и какие копии уже отдали в этом сеансе — чтобы не слать одно и то же при каждой встрече.
    private readonly HashSet<(Guid Packet, Guid Peer)> _relayHanded = new();

    private static bool IsRelayable(string? type) =>
        type is ChatPacket.ReminderShare or ChatPacket.ReminderDelete or ChatPacket.ReminderDone;

    /// <summary>Получатель пакета не в сети — раздать копии тем, кто в сети (или одному <paramref name="onlyTo"/>).</summary>
    private void OfferRelay(OutboxItem item, Peer? onlyTo = null)
    {
        if (_discovery.Find(item.PeerId) is { IsOnline: true }) return;
        ChatPacket? packet;
        try
        {
            packet = JsonSerializer.Deserialize<ChatPacket>(item.PacketJson);
        }
        catch (JsonException)
        {
            return;
        }
        if (packet == null || !IsRelayable(packet.Type)) return;
        // Подписываем копию сразу: передавать её будут другие, а автор — мы.
        Stamp(packet);
        HandOver(item.PeerId, packet.From, packet.Id, JsonSerializer.Serialize(packet), onlyTo);
    }

    private void HandOver(Guid targetId, Guid originId, Guid packetId, string packetJson, Peer? onlyTo)
    {
        var holders = onlyTo != null
            ? new List<Peer> { onlyTo }
            : _contactsByPeer.Values.Where(c => c.IsOnline).Select(c => c.Peer!).ToList();
        foreach (var holder in holders)
        {
            if (holder.Id == targetId || holder.Id == originId || holder.Id == MyId) continue;
            if (!_relayHanded.Add((packetId, holder.Id))) continue;
            _ = SendRelayCopyAsync(holder, targetId, packetId, packetJson);
        }
    }

    private async Task SendRelayCopyAsync(Peer holder, Guid targetId, Guid packetId, string packetJson)
    {
        var packet = new ChatPacket { Type = ChatPacket.Relay, Id = packetId, TargetId = targetId, Text = packetJson };
        Stamp(packet);
        if (await _messaging.SendAsync(holder.Address, holder.Port, packet))
            MarkReachable(holder);
        else
            _relayHanded.Remove((packetId, holder.Id)); // не дошло — попробуем при следующей встрече
    }

    /// <summary>
    /// Кто-то появился в сети: отдать ему копии своих и хранимых пакетов для тех, кто всё ещё не в сети.
    /// (Если он сам получатель — пакеты уйдут обычной доставкой.)
    /// </summary>
    private void OfferRelaysTo(Peer peer)
    {
        foreach (var item in _outbox.Where(o => o.PeerId != peer.Id).ToList())
            OfferRelay(item, peer);
        foreach (var item in _store.PendingRelays())
        {
            if (item.TargetId == peer.Id || _discovery.Find(item.TargetId) is { IsOnline: true }) continue;
            HandOver(item.TargetId, item.OriginId, item.Id, item.PacketJson, peer);
        }
    }

    /// <summary>Пришла копия: для нас — обрабатываем как обычный пакет, для другого — храним до его появления.</summary>
    private void OnRelay(ChatPacket packet)
    {
        ChatPacket? inner;
        try
        {
            inner = JsonSerializer.Deserialize<ChatPacket>(packet.Text);
        }
        catch (JsonException)
        {
            return;
        }
        if (inner == null || !IsRelayable(inner.Type) || inner.From == MyId || inner.From == Guid.Empty) return;

        if (packet.TargetId == MyId)
        {
            Log.Info($"Получен пакет «{inner.Type}» от «{inner.FromName}», переданный через «{packet.FromName}»");
            DispatchReminderPacket(inner.From, inner);
            return;
        }

        if (!_store.AddRelay(new RelayItem(inner.Id, packet.TargetId, inner.From, packet.Text))) return;
        Log.Info($"Взяли на хранение пакет «{inner.Type}» от «{inner.FromName}» для {packet.TargetId}");
        _relayHanded.Add((inner.Id, packet.From)); // отправителю копию возвращать незачем
        if (_discovery.Find(packet.TargetId) is { IsOnline: true })
            _ = FlushAsync(packet.TargetId);
    }

    /// <summary>Передать получателю всё, что храним для него. false — не всё ушло.</summary>
    private async Task<bool> FlushRelaysAsync(Peer peer)
    {
        foreach (var item in _store.PendingRelays(peer.Id))
        {
            var packet = new ChatPacket { Type = ChatPacket.Relay, Id = item.Id, TargetId = peer.Id, Text = item.PacketJson };
            Stamp(packet);
            if (!await _messaging.SendAsync(peer.Address, peer.Port, packet)) return false;
            MarkReachable(peer);
            _store.MarkRelayDelivered(item.Id);
            Log.Info($"Передали за другого пакет {item.Id} получателю «{peer.Name}»");
        }
        return true;
    }

    /// <summary>Пакет напоминаний может прийти несколько раз (напрямую и через других) — обрабатываем один.</summary>
    private void DispatchReminderPacket(Guid from, ChatPacket packet)
    {
        if (packet.Id != Guid.Empty && !_store.MarkSeen(packet.Id)) return;
        ReminderPacketReceived?.Invoke(from, packet);
    }
}
