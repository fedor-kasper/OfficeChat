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

    /// <summary>Наш TCP-порт сообщений — сообщаем его остальным в каждом пакете обнаружения.</summary>
    public int MessagingPort { get; set; } = Peer.DefaultMessagingPort;

    /// <summary>Наша версия, система и можно ли взять у нас обновление — тоже сообщаем остальным.</summary>
    public string AppVersion { get; set; } = "";
    public string Platform { get; set; } = "";
    public bool CanShareUpdate { get; set; }

    public DiscoveryService(AppSettings settings)
    {
        _settings = settings;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
    }

    public void Start()
    {
        try
        {
            _udp = CreateSocket(DiscoveryPort);
        }
        catch (SocketException ex)
        {
            // Порт занят копией OfficeChat другого пользователя Windows на этом компьютере.
            // Рассылки остальных до нас тогда не дойдут, но наши «я здесь» уходят как обычно,
            // а остальные отвечают на них прямо на наш порт — так мы их и видим.
            Log.Warn($"UDP-порт {DiscoveryPort} занят ({ex.SocketErrorCode}) — берём свободный, " +
                     "других будем узнавать по ответам на наши рассылки");
            _udp = CreateSocket(0);
        }
        Log.Info($"Обнаружение: слушаем UDP-порт {((IPEndPoint)_udp.Client.LocalEndPoint!).Port}, " +
                 $"сообщаем порт сообщений {MessagingPort}");

        _ = ReceiveLoopAsync(_cts.Token);
        _ = AnnounceLoopAsync(_cts.Token);
    }

    private static UdpClient CreateSocket(int port)
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            udp.EnableBroadcast = true;
            return udp;
        }
        catch
        {
            udp.Dispose();
            throw;
        }
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

            var sender = result.RemoteEndPoint;
            _uiContext.Post(_ => HandlePacket(packet, sender), null);
        }
    }

    // Выполняется в UI-потоке, поэтому _peers не требует блокировок.
    private void HandlePacket(DiscoveryPacket packet, IPEndPoint sender)
    {
        var from = sender.Address;
        if (packet.Type == DiscoveryPacket.Bye)
        {
            if (_peers.TryGetValue(packet.Id, out var gone))
                MarkOffline(gone);
            return;
        }

        var cameOnline = Observe(packet.Id, packet.Name, packet.Machine, from, packet.Port);
        if (_peers.TryGetValue(packet.Id, out var peer))
        {
            peer.AppVersion = packet.Version;
            peer.Platform = packet.Platform;
            peer.CanShareUpdate = packet.CanShareUpdate;
        }

        // Отвечаем напрямую на адрес и порт отправителя:
        // - новичку — чтобы он увидел нас сразу, не дожидаясь нашей рассылки;
        // - тому, кто сидит не на стандартном порту (второй пользователь Windows на компьютере), — всегда,
        //   иначе он не услышит ничьих рассылок и через 10 секунд решит, что все ушли.
        if (packet.Type == DiscoveryPacket.Hello && (cameOnline || sender.Port != DiscoveryPort))
            Send(CreatePacket(DiscoveryPacket.Reply), sender);
    }

    /// <summary>
    /// Отмечает, что компьютер точно в сети (пришёл пакет обнаружения или сообщение).
    /// Возвращает true, если он только что появился. Вызывать только из UI-потока.
    /// </summary>
    /// <param name="messagingPort">Порт сообщений из пакета: null — не менять, 0 — старая версия (стандартный порт).</param>
    public bool Observe(Guid id, string name, string machine, IPAddress from, int? messagingPort = null)
    {
        if (!_peers.TryGetValue(id, out var peer))
        {
            peer = new Peer { Id = id };
            _peers.Add(id, peer);
        }

        if (!string.IsNullOrWhiteSpace(name)) peer.Name = name;
        if (!string.IsNullOrWhiteSpace(machine)) peer.Machine = machine;
        peer.Address = from;
        // 0 — старая версия программы, она всегда на стандартном порту.
        if (messagingPort is { } port)
            peer.Port = port > 0 ? port : Peer.DefaultMessagingPort;
        peer.LastSeen = DateTime.UtcNow;

        if (peer.IsOnline) return false;
        peer.IsOnline = true;
        Log.Info($"В сети: «{peer.Name}» ({peer.Machine}, {from}:{peer.Port})");
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
        Log.Info($"Не в сети: «{peer.Name}» ({peer.Machine})");
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
        Port = MessagingPort,
        Version = AppVersion,
        Platform = Platform,
        CanShareUpdate = CanShareUpdate,
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

    /// <summary>TCP-порт сообщений отправителя (0 у старых версий — стандартный).</summary>
    public int Port { get; set; }

    /// <summary>Версия программы, система и можно ли взять у отправителя обновление (у старых версий пусто).</summary>
    public string Version { get; set; } = "";
    public string Platform { get; set; } = "";
    public bool CanShareUpdate { get; set; }
}
