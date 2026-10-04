using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using OfficeChat.Models;

namespace OfficeChat.Services;

/// <summary>
/// Поиск других компьютеров без сервера: каждый экземпляр раз в несколько секунд
/// рассылает UDP broadcast «я здесь» и слушает такие же сообщения от остальных.
/// </summary>
public sealed class DiscoveryService : IDisposable
{
    public const int DiscoveryPort = 45678;

    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(10);

    private readonly AppSettings _settings;
    private readonly SynchronizationContext _uiContext;
    private readonly Dictionary<Guid, Peer> _peers = new();
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;

    /// <summary>Компьютер появился в сети или вернулся (вызывается в UI-потоке).</summary>
    public event Action<Peer>? PeerOnline;

    /// <summary>Компьютер ушёл из сети или закрыл программу (вызывается в UI-потоке).</summary>
    public event Action<Peer>? PeerOffline;

    public DiscoveryService(AppSettings settings)
    {
        _settings = settings;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    public void Start()
    {
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        _udp.EnableBroadcast = true;

        _ = ReceiveLoopAsync(_cts.Token);
        _ = AnnounceLoopAsync(_cts.Token);
    }

    /// <summary>Сразу разослать актуальные данные о себе (например, после смены имени).</summary>
    public void AnnounceNow() => Broadcast(CreatePacket(DiscoveryPacket.Hello));

    private async Task AnnounceLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(AnnounceInterval);
        do
        {
            AnnounceNow();
            RemoveStalePeers();
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await _udp!.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }

            DiscoveryPacket? packet;
            try
            {
                packet = JsonSerializer.Deserialize<DiscoveryPacket>(result.Buffer);
            }
            catch (JsonException) { continue; }

            if (packet == null || packet.App != DiscoveryPacket.AppTag || packet.Id == _settings.UserId)
                continue;

            _uiContext.Post(_ => HandlePacket(packet, result.RemoteEndPoint.Address), null);
        }
    }

    // Выполняется в UI-потоке, поэтому _peers не требует блокировок.
    private void HandlePacket(DiscoveryPacket packet, IPAddress from)
    {
        if (packet.Type == DiscoveryPacket.Bye)
        {
            if (_peers.TryGetValue(packet.Id, out var gone))
                MarkOffline(gone);
            return;
        }

        var cameOnline = Observe(packet.Id, packet.Name, packet.Machine, from);

        // Новичку отвечаем напрямую, чтобы он увидел нас сразу, не дожидаясь нашей рассылки.
        if (cameOnline && packet.Type == DiscoveryPacket.Hello)
            Send(CreatePacket(DiscoveryPacket.Reply), new IPEndPoint(from, DiscoveryPort));
    }

    /// <summary>
    /// Отмечает, что компьютер точно в сети (пришёл пакет обнаружения или сообщение).
    /// Возвращает true, если он только что появился. Вызывать только из UI-потока.
    /// </summary>
    public bool Observe(Guid id, string name, string machine, IPAddress from)
    {
        if (!_peers.TryGetValue(id, out var peer))
        {
            peer = new Peer { Id = id };
            _peers.Add(id, peer);
        }

        if (!string.IsNullOrWhiteSpace(name)) peer.Name = name;
        if (!string.IsNullOrWhiteSpace(machine)) peer.Machine = machine;
        peer.Address = from;
        peer.LastSeen = DateTime.UtcNow;

        if (peer.IsOnline) return false;
        peer.IsOnline = true;
        PeerOnline?.Invoke(peer);
        return true;
    }

    /// <summary>Добавляет собеседника из истории; он считается не в сети, пока не объявится.</summary>
    public Peer AddKnown(Guid id, string name, string machine, IPAddress lastAddress)
    {
        if (_peers.TryGetValue(id, out var existing)) return existing;
        var peer = new Peer { Id = id, Name = name, Machine = machine, Address = lastAddress };
        _peers.Add(id, peer);
        return peer;
    }

    /// <summary>Забывает компьютер, который не в сети (ручное удаление из списка).</summary>
    public void Forget(Guid id)
    {
        if (_peers.TryGetValue(id, out var peer) && !peer.IsOnline)
            _peers.Remove(id);
    }

    /// <summary>Компьютер по идентификатору (в том числе ушедший из сети), если он уже встречался.</summary>
    public Peer? Find(Guid id) => _peers.GetValueOrDefault(id);

    private void MarkOffline(Peer peer)
    {
        if (!peer.IsOnline) return;
        peer.IsOnline = false;
        PeerOffline?.Invoke(peer);
    }

    private void RemoveStalePeers()
    {
        _uiContext.Post(_ =>
        {
            var deadline = DateTime.UtcNow - PeerTimeout;
            foreach (var stale in _peers.Values.Where(p => p.IsOnline && p.LastSeen < deadline).ToList())
                MarkOffline(stale);
        }, null);
    }

    private DiscoveryPacket CreatePacket(string type) => new()
    {
        Type = type,
        Id = _settings.UserId,
        Name = _settings.DisplayName,
        Machine = Environment.MachineName,
    };

    private void Broadcast(DiscoveryPacket packet)
    {
        foreach (var address in GetBroadcastAddresses())
            Send(packet, new IPEndPoint(address, DiscoveryPort));
    }

    private void Send(DiscoveryPacket packet, IPEndPoint target)
    {
        try
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(packet);
            _udp?.Send(data, data.Length, target);
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// Общий broadcast 255.255.255.255 плюс адрес broadcast каждой активной сетевой карты —
    /// на Windows с несколькими адаптерами общий broadcast уходит только через один из них.
    /// </summary>
    private static IEnumerable<IPAddress> GetBroadcastAddresses()
    {
        var result = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (var i = 0; i < 4; i++)
                        broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    result.Add(new IPAddress(broadcast));
                }
            }
        }
        catch (NetworkInformationException) { }
        return result;
    }

    public void Dispose()
    {
        // Сообщаем остальным, что уходим, чтобы нас сразу убрали из списка.
        Broadcast(CreatePacket(DiscoveryPacket.Bye));
        _cts.Cancel();
        _udp?.Dispose();
        _cts.Dispose();
    }
}

/// <summary>Пакет обнаружения, передаётся как JSON.</summary>
public sealed class DiscoveryPacket
{
    public const string AppTag = "OfficeChat/1";
    public const string Hello = "hello";
    public const string Reply = "reply";
    public const string Bye = "bye";

    public string App { get; set; } = AppTag;
    public string Type { get; set; } = Hello;
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Machine { get; set; } = "";
}
