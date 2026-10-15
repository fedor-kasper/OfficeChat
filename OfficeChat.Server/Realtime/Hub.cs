using System.Collections.Concurrent;
using OfficeChat.Protocol;
using OfficeChat.Server.Data;

namespace OfficeChat.Server.Realtime;

/// <summary>
/// Подключённые устройства. Будит их, когда в журнале их аккаунта появились события, рассылает
/// то, что не хранится (кто в сети, «печатает…», ходы в играх), и отключает отозванные устройства.
/// </summary>
public sealed class Hub(ServerDb db, ILogger<Hub> log)
{
    private readonly ConcurrentDictionary<Guid, ClientConnection> _connections = new();

    public void Register(ClientConnection connection)
    {
        var firstDevice = !IsOnline(connection.AccountId);
        _connections[connection.DeviceId] = connection;
        log.LogInformation("Подключено устройство «{Device}» ({Account}), всего подключений: {Count}",
            connection.DeviceName, connection.AccountLogin, _connections.Count);
        if (firstDevice)
            Broadcast(new Frame { Op = Ops.Presence, AccountId = connection.AccountId, Online = true });
    }

    public void Unregister(ClientConnection connection)
    {
        if (!_connections.TryRemove(new KeyValuePair<Guid, ClientConnection>(connection.DeviceId, connection))) return;
        db.TouchDevice(connection.DeviceId);
        if (IsOnline(connection.AccountId)) return;
        db.TouchAccount(connection.AccountId);
        Broadcast(new Frame { Op = Ops.Presence, AccountId = connection.AccountId, Online = false });
    }

    public bool IsOnline(Guid accountId) => _connections.Values.Any(c => c.AccountId == accountId);

    public HashSet<Guid> OnlineAccounts() => _connections.Values.Select(c => c.AccountId).ToHashSet();

    /// <summary>В журналах этих аккаунтов новые события — пусть их устройства заберут.</summary>
    public void NotifyEvents(IEnumerable<Guid> accounts)
    {
        var set = accounts as ISet<Guid> ?? accounts.ToHashSet();
        foreach (var connection in _connections.Values.Where(c => set.Contains(c.AccountId)))
            connection.Wake();
    }

    /// <summary>Кадр без журнала — только тем, кто сейчас в сети.</summary>
    public void SendEphemeral(IEnumerable<Guid> accounts, Frame frame, Guid? exceptDevice = null)
    {
        var set = accounts.ToHashSet();
        foreach (var connection in _connections.Values.Where(c => set.Contains(c.AccountId) && c.DeviceId != exceptDevice))
            connection.Enqueue(frame);
    }

    public void Broadcast(Frame frame)
    {
        foreach (var connection in _connections.Values)
            connection.Enqueue(frame);
    }

    /// <summary>Отключить устройство (или все устройства аккаунта): вышли, сбросили пароль, заблокировали.</summary>
    public void Disconnect(Guid accountId, Guid? deviceId = null)
    {
        foreach (var connection in _connections.Values.Where(c => c.AccountId == accountId && (deviceId == null || c.DeviceId == deviceId)))
            connection.Close();
    }

    /// <summary>
    /// Отключить устройства, отозванные в обход веб-API (команды сервера в консоли работают в другом процессе).
    /// </summary>
    public void DisconnectRevoked()
    {
        if (_connections.IsEmpty) return;
        var active = db.ActiveDevices();
        foreach (var connection in _connections.Values.Where(c => !active.Contains(c.DeviceId)))
        {
            log.LogInformation("Устройство «{Device}» ({Account}) отозвано — отключаем", connection.DeviceName, connection.AccountLogin);
            connection.Close();
        }
    }
}

/// <summary>Раз в полминуты сверяет подключённые устройства с базой.</summary>
public sealed class RevocationWatcher(Hub hub, ILogger<RevocationWatcher> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                hub.DisconnectRevoked();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Не удалось проверить отозванные устройства");
            }
        }
    }
}
