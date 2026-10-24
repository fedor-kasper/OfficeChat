using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OfficeChat.Models;

namespace OfficeChat.Services;

/// <summary>Другой компьютер кассы, который сейчас виден в сети.</summary>
public sealed record CashPeer(Guid DeviceId, string Machine, string Author, DateTime LastSeen, bool InSync);

/// <summary>Как служба кассы обменивается данными с другими компьютерами (в тестах — подмена).</summary>
public interface ICashTransport : IDisposable
{
    /// <summary>Пришёл пакет (может вызываться из любого потока).</summary>
    event Action<byte[], IPEndPoint>? Received;

    void Start();
    void Broadcast(byte[] data);
    void Send(byte[] data, IPEndPoint to);
}

/// <summary>
/// Касса: поставщики и их баланс. Всё хранится журналом записей (поставка, возврат, погашение, отмена…),
/// который только дополняется. Компьютеры с одинаковым кодом кассы находят друг друга в локальной сети
/// (UDP, порт 45683) и обмениваются недостающими записями — сервер не нужен, а работать можно и без сети:
/// записи догонят остальных, когда связь появится. Пакеты зашифрованы ключом из кода кассы (AES-GCM):
/// чужие компьютеры их не прочитают и не подделают. Работает в UI-потоке.
/// </summary>
public sealed class CashService : IDisposable
{
    public const int SyncPort = 45683;

    private static readonly TimeSpan HelloInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FullResendCooldown = TimeSpan.FromMinutes(10);
    private static readonly byte[] Magic = "OCC1"u8.ToArray();
    private const int OpsPerPacket = 20;
    private const int OpsPerRound = 400;

    private readonly AppSettings _settings;
    private readonly CashStore _store;
    private readonly SynchronizationContext _ui;
    private readonly Func<ICashTransport> _createTransport;
    private readonly List<CashOperation> _ops = new();
    private readonly HashSet<Guid> _ids = new();
    // Номера записей по компьютерам и сколько из них подряд с первого (это и сообщаем остальным).
    private readonly Dictionary<Guid, HashSet<long>> _seqs = new();
    private readonly Dictionary<Guid, long> _contiguous = new();
    private readonly Dictionary<Guid, Supplier> _suppliers = new();
    private readonly Dictionary<Guid, PeerState> _peers = new();
    private ICashTransport? _transport;
    private UiTimer? _timer;
    private byte[]? _key;

    /// <summary>Поставщики по алфавиту (убранные из списка не показываются).</summary>
    public ObservableCollection<Supplier> Suppliers { get; } = new();

    /// <summary>Сумма балансов всех поставщиков.</summary>
    public long Total => Suppliers.Sum(s => s.Balance);

    /// <summary>Что-то поменялось в журнале (своя запись или пришла с другого компьютера).</summary>
    public event Action? Changed;

    /// <summary>Поменялся список компьютеров кассы в сети или их синхронность.</summary>
    public event Action? PeersChanged;

    /// <summary>Сообщать о себе по таймеру (в тестах — вручную через <see cref="SyncNow"/>).</summary>
    public bool AutoSync { get; init; } = true;

    /// <summary>Синхронизация включена (задан код кассы).</summary>
    public bool IsSyncing => _transport != null;

    public CashService(AppSettings settings, string? databasePath = null, Func<ICashTransport>? transport = null,
        SynchronizationContext? context = null)
    {
        _settings = settings;
        _store = new CashStore(databasePath);
        _ui = context ?? SynchronizationContext.Current ?? new SynchronizationContext();
        _createTransport = transport ?? (() => new UdpCashTransport());
        foreach (var op in _store.LoadAll()) Remember(op);
        Rebuild();
    }

    /// <summary>Включить синхронизацию по коду кассы из настроек (без кода — касса только на этом компьютере).</summary>
    public void Start()
    {
        Stop();
        if (string.IsNullOrWhiteSpace(_settings.CashCode)) return;
        _key = DeriveKey(_settings.CashCode);
        try
        {
            _transport = _createTransport();
            _transport.Received += OnReceived;
            _transport.Start();
        }
        catch (SocketException ex)
        {
            Log.Warn("Касса: не удалось открыть порт синхронизации", ex);
            _transport?.Dispose();
            _transport = null;
            return;
        }
        if (AutoSync)
        {
            _timer = new UiTimer { Interval = HelloInterval };
            _timer.Tick += (_, _) => SyncNow();
            _timer.Start();
        }
        SyncNow();
        Log.Info("Касса: синхронизация включена");
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        if (_transport != null)
        {
            _transport.Received -= OnReceived;
            _transport.Dispose();
            _transport = null;
        }
        if (_peers.Count > 0)
        {
            _peers.Clear();
            PeersChanged?.Invoke();
        }
    }

    /// <summary>Сообщить о себе остальным прямо сейчас (и убрать из списка тех, кого давно не слышно).</summary>
    public void SyncNow()
    {
        if (_transport == null) return;
        var stale = _peers.Where(p => DateTime.UtcNow - p.Value.LastSeen > PeerTimeout).Select(p => p.Key).ToList();
        foreach (var id in stale) _peers.Remove(id);
        if (stale.Count > 0) PeersChanged?.Invoke();
        if (Seal(Hello()) is { } data) _transport.Broadcast(data);
    }

    /// <summary>Компьютеры кассы, которые сейчас в сети.</summary>
    public List<CashPeer> Peers => _peers.Values
        .Select(p => new CashPeer(p.DeviceId, p.Machine, p.Author, p.LastSeen, InSyncWith(p.Vector, p.Count)))
        .OrderBy(p => p.Machine).ToList();

    // ---- Записи ----

    public Supplier AddSupplier(string name)
    {
        var op = Record(new CashOperation { Kind = CashKind.SupplierAdded, SupplierId = Guid.NewGuid(), Name = Clean(name, 100) });
        return _suppliers[op.SupplierId];
    }

    public void Rename(Supplier supplier, string name)
    {
        name = Clean(name, 100);
        if (name.Length > 0 && name != supplier.Name)
            Record(new CashOperation { Kind = CashKind.SupplierRenamed, SupplierId = supplier.Id, Name = name });
    }

    /// <summary>Убрать поставщика из списка — только если по нему нет действующих записей о деньгах.</summary>
    public bool Remove(Supplier supplier)
    {
        if (!supplier.CanRemove) return false;
        Record(new CashOperation { Kind = CashKind.SupplierRemoved, SupplierId = supplier.Id });
        return true;
    }

    /// <summary>Поставка: по накладной <paramref name="due"/>, выдали <paramref name="paid"/> (копейки).</summary>
    public CashOperation RecordDelivery(Supplier supplier, long due, long paid, string comment) =>
        Record(new CashOperation { Kind = CashKind.Delivery, SupplierId = supplier.Id, Due = due, Paid = paid, Comment = Clean(comment, 500) });

    public CashOperation RecordReturn(Supplier supplier, long amount, string comment) =>
        Record(new CashOperation { Kind = CashKind.Return, SupplierId = supplier.Id, Amount = amount, Comment = Clean(comment, 500) });

    public CashOperation RecordPayout(Supplier supplier, long amount, string comment) =>
        Record(new CashOperation { Kind = CashKind.Payout, SupplierId = supplier.Id, Amount = amount, Comment = Clean(comment, 500) });

    /// <summary>Отменить ошибочную запись: она остаётся в истории зачёркнутой, с тем, кто и почему отменил.</summary>
    public bool Cancel(CashEntry entry, string reason)
    {
        if (entry.IsCancelled) return false;
        Record(new CashOperation
        {
            Kind = CashKind.Cancel, SupplierId = entry.Operation.SupplierId, TargetId = entry.Operation.Id, Comment = Clean(reason, 500),
        });
        return true;
    }

    private CashOperation Record(CashOperation template)
    {
        var op = template with
        {
            Id = Guid.NewGuid(),
            DeviceId = _settings.CashDeviceId,
            Seq = _contiguous.GetValueOrDefault(_settings.CashDeviceId) + 1,
            Author = _settings.DisplayName,
            Machine = Environment.MachineName,
            CreatedAt = DateTimeOffset.Now,
        };
        // Номер с этого компьютера уже занят (в журнале есть «дыры» — копия папки на другом компьютере?) — берём следующий свободный.
        while (_seqs.TryGetValue(op.DeviceId, out var used) && used.Contains(op.Seq)) op = op with { Seq = op.Seq + 1 };
        _store.Add(op);
        Remember(op);
        Rebuild();
        Log.Info($"Касса: {op.Kind} {Money.FormatSigned(op.Delta)} ({op.SupplierId})");
        Changed?.Invoke();
        RefreshPeerSync();
        // Сразу — тем, кто в сети, не дожидаясь очередного обмена.
        if (_transport != null && Seal(new CashPacket { Type = CashPacket.OpsType, DeviceId = _settings.CashDeviceId, Ops = [op] }) is { } data)
            foreach (var peer in _peers.Values)
                _transport.Send(data, peer.EndPoint);
        return op;
    }

    private static string Clean(string? text, int max)
    {
        var value = (text ?? "").Trim();
        return value.Length > max ? value[..max] : value;
    }

    // ---- Журнал → поставщики ----

    private bool Remember(CashOperation op)
    {
        if (!_ids.Add(op.Id)) return false;
        _ops.Add(op);
        if (!_seqs.TryGetValue(op.DeviceId, out var seqs)) _seqs[op.DeviceId] = seqs = new HashSet<long>();
        seqs.Add(op.Seq);
        var contiguous = _contiguous.GetValueOrDefault(op.DeviceId);
        while (seqs.Contains(contiguous + 1)) contiguous++;
        _contiguous[op.DeviceId] = contiguous;
        return true;
    }

    /// <summary>Пересчитать поставщиков и балансы по всему журналу (записей немного — это быстро).</summary>
    private void Rebuild()
    {
        var ordered = _ops.OrderBy(o => o.CreatedAt).ThenBy(o => o.DeviceId).ThenBy(o => o.Seq).ToList();
        var cancels = new Dictionary<Guid, CashOperation>();
        foreach (var op in ordered.Where(o => o.Kind == CashKind.Cancel)) cancels.TryAdd(op.TargetId, op);

        var removed = new HashSet<Guid>();
        foreach (var supplier in _suppliers.Values) supplier.Entries.Clear();
        foreach (var op in ordered)
        {
            if (op.Kind == CashKind.Cancel) continue;
            if (!_suppliers.TryGetValue(op.SupplierId, out var supplier))
                _suppliers[op.SupplierId] = supplier = new Supplier { Id = op.SupplierId, Name = "(поставщик с другого компьютера)" };
            switch (op.Kind)
            {
                case CashKind.SupplierAdded:
                case CashKind.SupplierRenamed:
                    if (op.Name.Length > 0) supplier.Name = op.Name;
                    removed.Remove(op.SupplierId);
                    break;
                case CashKind.SupplierRemoved:
                    removed.Add(op.SupplierId);
                    break;
                default:
                    supplier.Entries.Add(new CashEntry { Operation = op, Cancellation = cancels.GetValueOrDefault(op.Id) });
                    break;
            }
            if (op.CreatedAt > supplier.LastActivity) supplier.LastActivity = op.CreatedAt;
        }

        foreach (var supplier in _suppliers.Values)
        {
            long balance = 0;
            foreach (var entry in supplier.Entries)
            {
                if (!entry.IsCancelled) balance += entry.Operation.Delta;
                entry.BalanceAfter = balance;
            }
            supplier.Balance = balance;
        }

        // Убранный из списка возвращается, если с другого компьютера пришли деньги по нему.
        var visible = _suppliers.Values
            .Where(s => !removed.Contains(s.Id) || s.Entries.Any(e => !e.IsCancelled))
            .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (var i = 0; i < visible.Count; i++)
        {
            var index = Suppliers.IndexOf(visible[i]);
            if (index == i) continue;
            if (index < 0) Suppliers.Insert(i, visible[i]);
            else Suppliers.Move(index, i);
        }
        while (Suppliers.Count > visible.Count) Suppliers.RemoveAt(Suppliers.Count - 1);
    }

    // ---- Синхронизация ----

    private sealed class PeerState
    {
        public Guid DeviceId { get; init; }
        public string Machine { get; set; } = "";
        public string Author { get; set; } = "";
        public DateTime LastSeen { get; set; }
        public IPEndPoint EndPoint { get; set; } = new(IPAddress.None, 0);
        public Dictionary<Guid, long> Vector { get; set; } = new();
        public int Count { get; set; }
        public bool WasInSync { get; set; }
        public DateTime LastHelloReply { get; set; }
        public DateTime LastFullRequest { get; set; }
    }

    private CashPacket Hello() => new()
    {
        Type = CashPacket.HelloType,
        DeviceId = _settings.CashDeviceId,
        Machine = Environment.MachineName,
        Author = _settings.DisplayName,
        Vector = new Dictionary<Guid, long>(_contiguous),
        Count = _ops.Count,
    };

    private bool InSyncWith(Dictionary<Guid, long> vector, int count) =>
        count == _ops.Count && _contiguous.All(p => vector.GetValueOrDefault(p.Key) == p.Value) &&
        vector.All(p => _contiguous.GetValueOrDefault(p.Key) == p.Value);

    private void OnReceived(byte[] data, IPEndPoint from)
    {
        if (Open(data) is not { } packet) return;
        _ui.Post(_ => Handle(packet, from), null);
    }

    /// <summary>Пакет от другого компьютера кассы (в UI-потоке).</summary>
    internal void Handle(CashPacket packet, IPEndPoint from)
    {
        if (_transport == null || packet.DeviceId == _settings.CashDeviceId || packet.DeviceId == Guid.Empty) return;
        switch (packet.Type)
        {
            case CashPacket.HelloType:
                OnHello(packet, from);
                break;
            case CashPacket.OpsType when packet.Ops != null:
                Receive(packet.Ops);
                break;
            case CashPacket.WantAllType:
                // У него столько же записей по номерам, но меньше по счёту — шлём всё, пусть сам разберётся.
                SendOps(_ops.OrderBy(o => o.DeviceId).ThenBy(o => o.Seq), from, int.MaxValue);
                break;
        }
    }

    private void OnHello(CashPacket hello, IPEndPoint from)
    {
        var isNew = !_peers.TryGetValue(hello.DeviceId, out var peer);
        if (peer == null) _peers[hello.DeviceId] = peer = new PeerState { DeviceId = hello.DeviceId };
        peer.Machine = hello.Machine;
        peer.Author = hello.Author;
        peer.LastSeen = DateTime.UtcNow;
        peer.EndPoint = from;
        peer.Vector = hello.Vector ?? new();
        peer.Count = hello.Count;
        if (isNew) Log.Info($"Касса: в сети {hello.Machine} ({hello.Author})");

        // Чего у него нет — отправляем.
        var vector = peer.Vector;
        SendOps(_ops.Where(o => o.Seq > vector.GetValueOrDefault(o.DeviceId)).OrderBy(o => o.DeviceId).ThenBy(o => o.Seq),
            from, OpsPerRound);

        // У него есть то, чего нет у нас, — сообщаем о себе ему напрямую, он пришлёт.
        var weLack = vector.Any(p => p.Value > _contiguous.GetValueOrDefault(p.Key));
        if (weLack && DateTime.UtcNow - peer.LastHelloReply > TimeSpan.FromSeconds(1) && Seal(Hello()) is { } reply)
        {
            peer.LastHelloReply = DateTime.UtcNow;
            _transport!.Send(reply, from);
        }
        // По номерам всё совпадает, а записей у него больше: у кого-то повторились номера — просим всё целиком.
        else if (!weLack && hello.Count > _ops.Count && DateTime.UtcNow - peer.LastFullRequest > FullResendCooldown &&
                 _contiguous.All(p => vector.GetValueOrDefault(p.Key) <= p.Value) &&
                 Seal(new CashPacket { Type = CashPacket.WantAllType, DeviceId = _settings.CashDeviceId }) is { } want)
        {
            peer.LastFullRequest = DateTime.UtcNow;
            Log.Warn($"Касса: у {hello.Machine} записей больше при тех же номерах — запрашиваем весь журнал");
            _transport!.Send(want, from);
        }

        if (!RefreshPeerSync() && isNew) PeersChanged?.Invoke();
    }

    /// <summary>Пересчитать «всё совпадает» по каждому компьютеру; true — что-то поменялось (и сообщили).</summary>
    private bool RefreshPeerSync()
    {
        var changed = false;
        foreach (var peer in _peers.Values)
        {
            var inSync = InSyncWith(peer.Vector, peer.Count);
            if (inSync == peer.WasInSync) continue;
            peer.WasInSync = inSync;
            changed = true;
        }
        if (changed) PeersChanged?.Invoke();
        return changed;
    }

    private void SendOps(IEnumerable<CashOperation> ops, IPEndPoint to, int limit)
    {
        foreach (var chunk in ops.Take(limit).Chunk(OpsPerPacket))
            if (Seal(new CashPacket { Type = CashPacket.OpsType, DeviceId = _settings.CashDeviceId, Ops = chunk.ToList() }) is { } data)
                _transport!.Send(data, to);
    }

    private void Receive(List<CashOperation> ops)
    {
        var added = 0;
        foreach (var op in ops)
        {
            if (!IsValid(op) || _ids.Contains(op.Id)) continue;
            if (op.DeviceId == _settings.CashDeviceId)
            {
                // Чужая запись с нашим номером компьютера: папку программы скопировали на другой компьютер.
                // Дальше пишем под новым номером, чтобы записи не путались.
                Log.Warn("Касса: другой компьютер пишет под нашим номером — меняем свой");
                _settings.CashDeviceId = Guid.NewGuid();
                SettingsService.Save(_settings);
            }
            if (!_store.Add(op)) continue;
            Remember(op);
            added++;
        }
        if (added == 0) return;
        Rebuild();
        Log.Info($"Касса: получено записей {added}");
        Changed?.Invoke();
        RefreshPeerSync();
    }

    private static bool IsValid(CashOperation op) =>
        op.Id != Guid.Empty && op.DeviceId != Guid.Empty && op.Seq > 0 && op.SupplierId != Guid.Empty &&
        Enum.IsDefined(op.Kind) && op.Due is >= 0 and <= Money.MaxAmount && op.Paid is >= 0 and <= Money.MaxAmount &&
        op.Amount is >= 0 and <= Money.MaxAmount && op.Name.Length <= 100 && op.Comment.Length <= 500 &&
        op.Author.Length <= 100 && op.Machine.Length <= 100 && (op.Kind != CashKind.Cancel || op.TargetId != Guid.Empty);

    // ---- Шифрование ----

    /// <summary>Ключ из кода кассы (медленная функция — подобрать код перебором трудно).</summary>
    private static byte[] DeriveKey(string code) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(code.Trim()), "OfficeChat cash v1"u8.ToArray(), 200_000,
            HashAlgorithmName.SHA256, 32);

    private byte[]? Seal(CashPacket packet)
    {
        if (_key == null) return null;
        var plain = JsonSerializer.SerializeToUtf8Bytes(packet);
        var result = new byte[Magic.Length + 12 + 16 + plain.Length];
        Magic.CopyTo(result, 0);
        var nonce = result.AsSpan(Magic.Length, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plain, result.AsSpan(Magic.Length + 28), result.AsSpan(Magic.Length + 12, 16));
        return result;
    }

    private CashPacket? Open(byte[] data)
    {
        var key = _key;
        if (key == null || data.Length < Magic.Length + 28 || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return null;
        var plain = new byte[data.Length - Magic.Length - 28];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(data.AsSpan(Magic.Length, 12), data.AsSpan(Magic.Length + 28), data.AsSpan(Magic.Length + 12, 16), plain);
            return JsonSerializer.Deserialize<CashPacket>(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null; // другая касса (другой код) или повреждённый пакет
        }
    }

    public void Dispose()
    {
        Stop();
        _store.Dispose();
    }
}

/// <summary>Пакет синхронизации кассы (внутри шифрования).</summary>
public sealed class CashPacket
{
    public const string HelloType = "hello";
    public const string OpsType = "ops";
    public const string WantAllType = "want-all";

    [JsonPropertyName("t")] public string Type { get; set; } = HelloType;
    [JsonPropertyName("dev")] public Guid DeviceId { get; set; }
    [JsonPropertyName("machine")] public string Machine { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    /// <summary>Сколько записей с каждого компьютера есть подряд с первой.</summary>
    [JsonPropertyName("vec")] public Dictionary<Guid, long>? Vector { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("ops")] public List<CashOperation>? Ops { get; set; }
}

/// <summary>Обмен по UDP в локальной сети: рассылка «я здесь» и пакеты напрямую.</summary>
public sealed class UdpCashTransport : ICashTransport
{
    private readonly CancellationTokenSource _cts = new();
    private UdpClient? _udp;

    public event Action<byte[], IPEndPoint>? Received;

    public void Start()
    {
        try
        {
            _udp = Create(CashService.SyncPort);
        }
        catch (SocketException ex)
        {
            // Порт занят (вторая копия программы на этом компьютере) — свои рассылки шлём, отвечают нам напрямую.
            Log.Warn($"Касса: UDP-порт {CashService.SyncPort} занят ({ex.SocketErrorCode}) — берём свободный");
            _udp = Create(0);
        }
        _ = ReceiveLoopAsync(_cts.Token);
    }

    private static UdpClient Create(int port)
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

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await _udp!.ReceiveAsync(ct).ConfigureAwait(false);
                Received?.Invoke(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { }
        }
    }

    public void Broadcast(byte[] data)
    {
        foreach (var address in DiscoveryService.GetBroadcastAddresses())
            Send(data, new IPEndPoint(address, CashService.SyncPort));
    }

    public void Send(byte[] data, IPEndPoint to)
    {
        try
        {
            _udp?.Send(data, data.Length, to);
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp?.Dispose();
        _cts.Dispose();
    }
}
