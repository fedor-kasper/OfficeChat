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
    private readonly ISupplierSite? _site;
    // Заказы, взятые с сайта, по поставщикам (в журнал попадают, только когда кассир записал оплату).
    private readonly Dictionary<Guid, List<ExternalOrder>> _siteOrders = new();
    private UiTimer? _siteTimer;
    private bool _refreshingSites;
    private bool _refreshAgain;
    private bool _disposed;
    private CancellationTokenSource _siteStop = new();
    private readonly SemaphoreSlim _siteRefreshGate = new(1, 1);

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

    /// <summary>Как часто заглядывать на сайты поставщиков.</summary>
    private static readonly TimeSpan SiteInterval = TimeSpan.FromMinutes(10);

    public CashService(AppSettings settings, string? databasePath = null, Func<ICashTransport>? transport = null,
        SynchronizationContext? context = null, ISupplierSite? site = null)
    {
        _settings = settings;
        _site = site;
        _store = new CashStore(databasePath);
        _ui = context ?? SynchronizationContext.Current ?? new SynchronizationContext();
        _createTransport = transport ?? (() => new UdpCashTransport());
        foreach (var op in _store.LoadAll()) Remember(op);
        Rebuild();
    }

    /// <summary>Включить синхронизацию по коду кассы из настроек (без кода — касса только на этом компьютере).</summary>
    public void Start()
    {
        if (_disposed) return;
        Stop();
        _siteStop = new CancellationTokenSource();
        if (AutoSync)
        {
            // Первый запрос сразу при запуске, дальше — каждые десять минут.
            _siteTimer = new UiTimer { Interval = SiteInterval };
            _siteTimer.Tick += async (_, _) => await RefreshSitesAsync();
            _siteTimer.Start();
            _ = RefreshSitesAsync();
        }
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
        _siteStop.Cancel();
        _refreshAgain = false;
        _timer?.Dispose();
        _timer = null;
        _siteTimer?.Dispose();
        _siteTimer = null;
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

    /// <summary>Поставка: по накладной <paramref name="due"/>, выдали <paramref name="paid"/> (копейки).
    /// <paramref name="external"/> — заказ с сайта поставщика, если оплачиваем его.</summary>
    public CashOperation RecordDelivery(Supplier supplier, long due, long paid, string comment, string external = "", CashPaymentMethod paymentMethod = CashPaymentMethod.Unspecified) =>
        Record(new CashOperation
        {
            Kind = CashKind.Delivery, SupplierId = supplier.Id, Due = due, Paid = paid, Comment = Clean(comment, 500), External = external, PaymentMethod = paymentMethod,
        });

    public CashOperation RecordReturn(Supplier supplier, long amount, string comment, string external = "") =>
        Record(new CashOperation
        {
            Kind = CashKind.Return, SupplierId = supplier.Id, Amount = amount, Comment = Clean(comment, 500), External = external,
        });

    /// <summary>Корректировка: баланс поставщика становится <paramref name="balance"/> (с этого момента).</summary>
    public CashOperation SetBalance(Supplier supplier, long balance, string reason) =>
        Record(new CashOperation { Kind = CashKind.BalanceSet, SupplierId = supplier.Id, Balance = balance, Comment = Clean(reason, 500) });

    /// <summary>Заказ с сайта не оплачиваем (например, оплачен иначе) — убрать из ожидающих, с причиной.</summary>
    public CashOperation SkipOrder(Supplier supplier, ExternalOrder order, string reason) =>
        Record(new CashOperation
        {
            Kind = CashKind.OrderSkipped, SupplierId = supplier.Id, External = order.External, Due = order.Sum,
            Comment = Clean($"Заказ №{order.Number} от {order.DateText} на {order.SumText}" + (reason.Length > 0 ? $": {reason}" : ""), 500),
        });

    /// <summary>
    /// Подключить поставщика к сайту: логин, пароль (хранится зашифрованным кодом кассы, уходит на все компьютеры кассы)
    /// и с какой даты учитывать заказы и возвраты. Пустой логин — отключить.
    /// </summary>
    public void Link(Supplier supplier, string login, string password, DateTime since, string siteId = ForumAutoSite.Id)
    {
        if (login.Length > 0) _ = SupplierSites.Create(siteId); // неизвестный сайт не сохраняем
        Record(new CashOperation
        {
            Kind = CashKind.SupplierLinked, SupplierId = supplier.Id, Name = login.Length > 0 ? siteId : "",
            Login = Clean(login, 100), Secret = login.Length > 0 ? ProtectSecret(password) : "", Since = new DateTimeOffset(since),
        });
        _siteOrders.Remove(supplier.Id);
        supplier.SiteBalance = null;
        supplier.SiteBalanceUpdatedAt = null;
        supplier.SitePayments.Clear();
        supplier.SiteStatus = login.Length > 0 ? "Подключено — загружаем заказы…" : "";
        Rebuild();
        Changed?.Invoke();
        if (login.Length > 0) _ = RefreshSitesAsync();
    }

    /// <summary>Пароль подключённого сайта (расшифрованный), null — не удалось (сменили код кассы?).</summary>
    public string? PasswordOf(Supplier supplier) => supplier.Link is { } link ? UnprotectSecret(link.Secret) : null;

    public CashOperation RecordPayout(Supplier supplier, long amount, string comment) =>
        Record(new CashOperation { Kind = CashKind.Payout, SupplierId = supplier.Id, Amount = amount, Comment = Clean(comment, 500) });

    /// <summary>Отменить ошибочную запись: она остаётся в истории зачёркнутой, с тем, кто и почему отменил.</summary>
    public bool Cancel(CashEntry entry, string reason)
    {
        if (!entry.CanCancel) return false;
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
        Log.Info($"Касса: {op.Kind} {Money.FormatSigned(op.Delta)} {op.External} ({op.SupplierId})");
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
        var previousLinks = _suppliers.ToDictionary(p => p.Key, p => p.Value.Link);
        var ordered = _ops.OrderBy(o => o.CreatedAt).ThenBy(o => o.DeviceId).ThenBy(o => o.Seq).ToList();
        var cancels = new Dictionary<Guid, CashOperation>();
        foreach (var op in ordered.Where(o => o.Kind == CashKind.Cancel)) cancels.TryAdd(op.TargetId, op);

        var removed = new HashSet<Guid>();
        foreach (var supplier in _suppliers.Values)
        {
            supplier.Entries.Clear();
            supplier.Recorded.Clear();
            supplier.Link = null;
        }
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
                case CashKind.SupplierLinked:
                    supplier.Link = op.Login.Length > 0 ? op : null;
                    break;
                default:
                    var entry = new CashEntry { Operation = op, Cancellation = cancels.GetValueOrDefault(op.Id) };
                    // Один заказ или возврат с сайта учитывается один раз (например, его записали на двух кассах сразу).
                    if (op.External.Length > 0 && !entry.IsCancelled && !supplier.Recorded.Add(op.External)) entry.IsDuplicate = true;
                    supplier.Entries.Add(entry);
                    break;
            }
            if (op.CreatedAt > supplier.LastActivity) supplier.LastActivity = op.CreatedAt;
        }

        foreach (var supplier in _suppliers.Values)
        {
            if (previousLinks.GetValueOrDefault(supplier.Id) != supplier.Link)
            {
                _siteOrders.Remove(supplier.Id);
                supplier.SiteBalance = null;
                supplier.SiteBalanceUpdatedAt = null;
                supplier.SitePayments = new();
            }
            long balance = 0;
            foreach (var entry in supplier.Entries)
            {
                var op = entry.Operation;
                entry.Delta = op.Kind == CashKind.BalanceSet ? op.Balance - balance : op.Delta;
                if (entry.Counts) balance += entry.Delta;
                entry.BalanceAfter = balance;
            }
            supplier.Balance = balance;
            UpdatePending(supplier);
        }

        // Убранный из списка возвращается, если с другого компьютера пришли деньги по нему.
        var visible = _suppliers.Values
            .Where(s => !removed.Contains(s.Id) || s.Entries.Any(e => e.Counts))
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

    // ---- Сайты поставщиков ----

    /// <summary>Ожидающие оплаты заказы: с сайта, не раньше даты начала учёта, не аннулированные и ещё не записанные.</summary>
    private void UpdatePending(Supplier supplier)
    {
        supplier.SiteLoaded = _siteOrders.ContainsKey(supplier.Id);
        if (supplier.Link?.Since is not { } since || !_siteOrders.TryGetValue(supplier.Id, out var orders))
        {
            supplier.PendingOrders = new();
            return;
        }
        // Поставки, записанные вручную (без номера заказа), — тоже оплата: такой заказ (та же сумма, записан после
        // оформления) больше не ждёт. Каждая ручная поставка закрывает один заказ.
        var manual = supplier.Entries
            .Where(e => e.Counts && e.Operation.Kind == CashKind.Delivery && e.Operation.External.Length == 0)
            .Select(e => e.Operation).ToList();
        var pending = new List<ExternalOrder>();
        foreach (var order in orders.Where(o => o.Date >= since.LocalDateTime && o.Sum > 0 && !IsCancelledStatus(o.Status) &&
                                                !supplier.Recorded.Contains(o.External)).OrderBy(o => o.Date))
        {
            var paid = manual.FirstOrDefault(m => m.Due == order.Sum && m.CreatedAt.LocalDateTime >= order.Date.AddHours(-1));
            if (paid != null) manual.Remove(paid);
            else pending.Add(order);
        }
        supplier.PendingOrders = pending;
    }

    private static bool IsCancelledStatus(string status) => status.Contains("Аннулир", StringComparison.OrdinalIgnoreCase);

    /// <summary>Возврат, который поставщик уже зачислил нам на баланс.</summary>
    public static bool IsCreditedReturn(ExternalReturn r) => r.Status.Contains("на баланс", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Заглянуть на сайты подключённых поставщиков: заказы — в список ожидающих оплаты, возвраты «Поставлен на баланс» —
    /// сразу в журнал (плюс к балансу). Ошибки — в строку состояния поставщика.
    /// </summary>
    public async Task RefreshSitesAsync()
    {
        if (_disposed || _siteStop.IsCancellationRequested) return;
        if (_refreshingSites || !_settings.CashMode)
        {
            if (_refreshingSites && _settings.CashMode) _refreshAgain = true;
            Log.Info($"Касса: обновление сайтов пропущено — {(_refreshingSites ? "уже выполняется" : "режим кассы выключен")}");
            return;
        }
        _refreshingSites = true;
        try
        {
            do
            {
                _refreshAgain = false;
                foreach (var supplier in _suppliers.Values.Where(s => s.Link != null && Suppliers.Contains(s)).ToList())
                {
                    if (_disposed || _siteStop.IsCancellationRequested) break;
                    var debug = new SiteDebug(supplier.Link!.Name);
                    debug.Line($"Автоматический отчёт: поставщик «{supplier.Name}», {supplier.Id}; учёт с {supplier.Link?.Since:O}");
                    try
                    {
                        await RefreshSupplierAsync(supplier, debug);
                        DescribePending(supplier, debug);
                    }
                    finally
                    {
                        debug.Line($"Состояние: {supplier.SiteStatus}; карточек в модели: {supplier.PendingOrders.Count}");
                        try { Log.Info($"Касса: диагностика сайта: {debug.Save(automatic: true)}"); }
                        catch (Exception ex) { Log.Warn("Касса: не удалось сохранить диагностику сайта", ex); }
                    }
                }
            } while (_refreshAgain && !_disposed && !_siteStop.IsCancellationRequested);
        }
        finally
        {
            _refreshingSites = false;
        }
    }

    /// <summary>
    /// Отладочный отчёт: сходить на сайт поставщика, записывая всё, что пришло и как разобрано, и почему каждый заказ
    /// ждёт или не ждёт оплаты. Возвращает путь к zip-файлу в папке журналов.
    /// </summary>
    public async Task<string> DebugSiteAsync(Supplier supplier)
    {
        var debug = new SiteDebug(supplier.Link?.Name ?? ForumAutoSite.Id);
        debug.Line($"Касса: компьютер {Environment.MachineName}, код кассы {(_settings.CashCode.Length > 0 ? "задан" : "не задан")}, " +
                   $"записей в журнале {_ops.Count}, поставщик «{supplier.Name}», баланс {Money.Format(supplier.Balance)}");
        if (supplier.Link is not { } link)
            debug.Line("Поставщик не подключён к сайту (кнопка «🌐 Сайт…»)");
        else
        {
            debug.Line($"Подключение: сайт {link.Name}, логин «{link.Login}», учёт с {link.Since?.LocalDateTime:dd.MM.yyyy}, " +
                       $"подключил {link.Author} ({link.Machine}) {link.CreatedAt.LocalDateTime:dd.MM.yyyy HH:mm}");
            debug.Line($"Уже учтены с сайта: {string.Join(", ", supplier.Recorded.DefaultIfEmpty("нет"))}");
            if (_refreshingSites) debug.Line("Внимание: в это время уже шло обычное обновление");
            await RefreshSupplierAsync(supplier, debug);
            debug.Line($"Состояние после обновления: «{supplier.SiteStatus}»");
            DescribePending(supplier, debug);
        }
        var path = debug.Save();
        Log.Info($"Касса: отладочный отчёт сайта сохранён: {path}");
        return path;
    }

    private async Task RefreshSupplierAsync(Supplier supplier, SiteDebug? debug)
    {
        debug?.Line("Ожидание очереди загрузки сайта");
        if (_disposed || _siteStop.IsCancellationRequested) return;
        var cancel = _siteStop.Token;
        try { await _siteRefreshGate.WaitAsync(cancel); }
        catch (OperationCanceledException) { debug?.Line("Загрузка отменена при остановке кассы"); return; }
        try
        {
            if (supplier.Link == null)
            {
                debug?.Line("Загрузка пропущена: поставщик отключён от сайта");
                return;
            }
            await RefreshSupplierCoreAsync(supplier, debug, cancel);
        }
        finally { _siteRefreshGate.Release(); }
    }

    private async Task RefreshSupplierCoreAsync(Supplier supplier, SiteDebug? debug, CancellationToken cancel)
    {
        var link = supplier.Link!;
        supplier.SiteBalanceStale = true;
        if (UnprotectSecret(link.Secret) is not { } password)
        {
            supplier.SiteStatus = "Не удалось расшифровать пароль сайта (сменили код кассы?) — подключите сайт заново";
            debug?.Line(supplier.SiteStatus);
            Changed?.Invoke();
            return;
        }
        try
        {
            var data = await (_site ?? SupplierSites.Create(link.Name)).FetchAsync(link.Login, password, link.Since?.LocalDateTime ?? DateTime.Today, cancel, debug);
            if (_disposed || cancel.IsCancellationRequested || supplier.Link != link)
            {
                debug?.Line("Ответ не применён: подключение поставщика изменилось во время загрузки");
                return;
            }
            _siteOrders[supplier.Id] = data.Orders;
            supplier.SiteBalance = data.Balance;
            supplier.SiteBalanceUpdatedAt = DateTime.Now;
            supplier.SiteBalanceSource = data.BalanceSource;
            supplier.SiteBalanceStale = false;
            supplier.SitePayments = data.Payments;
            debug?.Line($"Сверка: сайт={(data.Balance is { } b ? Money.Format(b) : "не получен")}; приложение={Money.Format(supplier.Balance)}");
            var added = ApplyReturns(supplier, data.Returns, debug);
            Rebuild();
            var since = link.Since?.Date ?? DateTime.Today;
            supplier.SiteStatus = $"Сайт: обновлено в {DateTime.Now:HH:mm} — заказов с {since:dd.MM}: {data.Orders.Count}, " +
                                  $"возвратов на баланс: {data.Returns.Count(IsCreditedReturn)}" +
                                  (added > 0 ? $" (новых: {added})" : "");
            Log.Info($"Касса: сайт {link.Login} — заказов {data.Orders.Count}, возвратов {data.Returns.Count}, новых возвратов {added}");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            debug?.Line("Загрузка отменена при остановке кассы");
            return;
        }
        catch (SiteException ex)
        {
            debug?.Line($"ОШИБКА сайта: {ex}");
            supplier.SiteStatus = $"Сайт ({DateTime.Now:HH:mm}): {ex.Message}";
            Log.Warn($"Касса: сайт {link.Login}: {ex.Message}", ex.InnerException);
        }
        catch (Exception ex) when (debug != null)
        {
            // В отладке любая ошибка — в отчёт (и на экран), а не падение программы.
            supplier.SiteStatus = $"Сайт ({DateTime.Now:HH:mm}): {ex.GetType().Name}: {ex.Message}";
            debug.Line($"НЕОЖИДАННАЯ ОШИБКА: {ex}");
            Log.Error("Касса: ошибка при отладке сайта", ex);
        }
        Changed?.Invoke();
    }

    /// <summary>Для отчёта: по каждому заказу с сайта — ждёт ли он оплаты, а если нет, то почему.</summary>
    private void DescribePending(Supplier supplier, SiteDebug debug)
    {
        if (!_siteOrders.TryGetValue(supplier.Id, out var orders))
        {
            debug.Line("Заказы с сайта не получены");
            return;
        }
        var since = supplier.Link?.Since?.LocalDateTime ?? DateTime.Today;
        debug.Line($"Заказов получено: {orders.Count}, ждут оплаты: {supplier.PendingOrders.Count}");
        foreach (var order in orders.OrderBy(o => o.Date))
        {
            var why = supplier.PendingOrders.Contains(order) ? "ЖДЁТ ОПЛАТЫ"
                : order.Date < since ? "раньше даты начала учёта"
                : order.Sum <= 0 ? "сумма 0"
                : IsCancelledStatus(order.Status) ? "аннулирован"
                : supplier.Recorded.Contains(order.External) ? "уже записан в журнале"
                : "закрыт поставкой, записанной вручную на ту же сумму";
            debug.Line($"  заказ {order.Number} {order.Date:dd.MM.yyyy HH:mm} «{order.Status}» {order.SumText}: {why}");
        }
    }

    /// <summary>Возвраты «Поставлен на баланс», которых ещё нет в журнале, — записать. Возвращает, сколько записали.</summary>
    private int ApplyReturns(Supplier supplier, List<ExternalReturn> returns, SiteDebug? debug = null)
    {
        var since = supplier.Link?.Since?.LocalDateTime ?? DateTime.Today;
        var added = 0;
        foreach (var item in returns.OrderBy(r => r.Date))
        {
            var why = !IsCreditedReturn(item) ? "статус не «на баланс»"
                : item.Date < since ? "раньше даты начала учёта"
                : item.Sum <= 0 ? "сумма не положительная"
                : supplier.Recorded.Contains(item.External) ? "уже записан в журнале" : null;
            debug?.Line($"Возврат {item.Number}, {item.Date:dd.MM.yyyy}, «{item.Status}», {Money.Format(item.Sum)}: {why ?? "записываем на баланс"}");
            if (why != null) continue;
            var penalty = item.Penalty is "" or "0" ? "" : $", штраф {item.Penalty}";
            RecordReturn(supplier, item.Sum, $"Возврат №{item.Number} от {item.Date:dd.MM.yyyy} ({item.Reason}{penalty}) — с сайта", item.External);
            added++;
        }
        return added;
    }

    // Пароль сайта — в журнале, зашифрованный кодом кассы: его видят только компьютеры этой кассы.
    private static byte[] SecretKey(string code) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(code.Trim()), "OfficeChat cash secret v1"u8.ToArray(), 100_000,
            HashAlgorithmName.SHA256, 32);

    private byte[]? _secretKey;
    private string? _secretKeyCode;

    private byte[] CurrentSecretKey()
    {
        if (_secretKey == null || _secretKeyCode != _settings.CashCode)
        {
            _secretKey = SecretKey(_settings.CashCode);
            _secretKeyCode = _settings.CashCode;
        }
        return _secretKey;
    }

    private string ProtectSecret(string secret)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        var result = new byte[12 + 16 + plain.Length];
        RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aes = new AesGcm(CurrentSecretKey(), 16);
        aes.Encrypt(result.AsSpan(0, 12), plain, result.AsSpan(28), result.AsSpan(12, 16));
        return Convert.ToBase64String(result);
    }

    private string? UnprotectSecret(string secret)
    {
        try
        {
            var data = Convert.FromBase64String(secret);
            if (data.Length < 28) return null;
            var plain = new byte[data.Length - 28];
            using var aes = new AesGcm(CurrentSecretKey(), 16);
            aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
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
        Enum.IsDefined(op.Kind) && Enum.IsDefined(op.PaymentMethod) && op.Due is >= 0 and <= Money.MaxAmount && op.Paid is >= 0 and <= Money.MaxAmount &&
        op.Amount is >= 0 and <= Money.MaxAmount && op.Name.Length <= 100 && op.Comment.Length <= 500 &&
        op.Author.Length <= 100 && op.Machine.Length <= 100 && (op.Kind != CashKind.Cancel || op.TargetId != Guid.Empty) &&
        op.Balance is >= -Money.MaxAmount and <= Money.MaxAmount && op.External.Length <= 100 && op.Login.Length <= 100 &&
        op.Secret.Length <= 1000;

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
        if (_disposed) return;
        _disposed = true;
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
