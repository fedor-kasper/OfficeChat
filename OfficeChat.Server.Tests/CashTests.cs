using System.Net;
using OfficeChat.Models;
using OfficeChat.Services;
using Xunit;

namespace OfficeChat.Server.Tests;

public class CashTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "officechat-cash-" + Guid.NewGuid().ToString("N"));
    private readonly FakeNetwork _network = new();
    private readonly List<CashService> _services = new();

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, true); } catch (IOException) { }
    }

    private CashService Create(string name, string code = "магазин-1", ISupplierSite? site = null)
    {
        var settings = new AppSettings { DisplayName = name, CashMode = true, CashCode = code };
        var service = new CashService(settings, Path.Combine(_folder, name + ".db"), () => _network.Join(), _network.Context, site)
            { AutoSync = false };
        service.Start();
        _services.Add(service);
        return service;
    }

    /// <summary>Несколько раундов обмена «я здесь», пока всё не разойдётся.</summary>
    private void Sync(int rounds = 3)
    {
        for (var i = 0; i < rounds; i++)
        {
            foreach (var service in _services) service.SyncNow();
            _network.Pump();
        }
    }

    [Theory]
    [InlineData("672.34", 67234L)]
    [InlineData("672,34", 67234L)]
    [InlineData("1 234", 123400L)]
    [InlineData("700 р", 70000L)]
    [InlineData("700руб.", 70000L)]
    [InlineData("0,5", 50L)]
    [InlineData("1.234", null)]
    [InlineData("-5", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void Money_is_parsed_in_kopecks(string text, long? expected) => Assert.Equal(expected, Money.Parse(text));

    [Fact]
    public void Money_is_formatted_in_russian()
    {
        Assert.Equal("1 234,56 ₽", Money.Format(123456).Replace(' ', ' ').Replace(' ', ' '));
        Assert.Equal("+27,66 ₽", Money.FormatSigned(2766));
        Assert.Equal("−20,00 ₽", Money.FormatSigned(-2000));
    }

    [Fact]
    public void Delivery_return_payout_and_cancel_make_the_balance()
    {
        var cash = Create("Касса");
        var milk = cash.AddSupplier("Молочный завод");

        cash.RecordDelivery(milk, due: 67234, paid: 70000, "");   // излишек +27,66
        cash.RecordReturn(milk, 15000, "просрочка");             // возврат +150
        cash.RecordPayout(milk, 10000, "учли в накладной");      // погашение −100
        var wrong = cash.RecordDelivery(milk, 50000, 60000, "");  // ошибка +100…
        Assert.Equal(2766 + 15000 - 10000 + 10000, milk.Balance);

        cash.Cancel(milk.Entries.Single(e => e.Operation.Id == wrong.Id), "ошиблись суммой"); // …отменили
        Assert.Equal(2766 + 15000 - 10000, milk.Balance);
        Assert.Equal(4, milk.Entries.Count); // отменённая запись остаётся в истории
        Assert.Equal(milk.Balance, cash.Total);
        Assert.False(milk.CanRemove);
    }

    [Fact]
    public void Cash_and_card_methods_sync_with_exact_amounts()
    {
        var first = Create("Первый");
        var second = Create("Второй");
        var supplier = first.AddSupplier("Авторусь");
        first.RecordDelivery(supplier, 17202, 17202, "", "autorus:order:1", CashPaymentMethod.Card);
        first.RecordDelivery(supplier, 17202, 20000, "", "autorus:order:2", CashPaymentMethod.Cash);
        Sync();
        var received = Assert.Single(second.Suppliers);
        Assert.Equal(2798, received.Balance);
        Assert.Equal(CashPaymentMethod.Card, received.Entries.Single(e => e.Operation.External.EndsWith(":1")).Operation.PaymentMethod);
        Assert.Equal(CashPaymentMethod.Cash, received.Entries.Single(e => e.Operation.External.EndsWith(":2")).Operation.PaymentMethod);
    }

    [Fact]
    public void Journal_survives_restart()
    {
        var cash = Create("Касса");
        var bread = cash.AddSupplier("Хлебозавод");
        cash.RecordDelivery(bread, 98010, 100000, "");
        cash.Rename(bread, "Хлебозавод №1");
        cash.Dispose();
        _services.Remove(cash);

        var again = Create("Касса");
        var restored = Assert.Single(again.Suppliers);
        Assert.Equal("Хлебозавод №1", restored.Name);
        Assert.Equal(1990, restored.Balance);
    }

    [Fact]
    public void Computers_with_the_same_code_share_the_journal()
    {
        var first = Create("Касса 1");
        var second = Create("Касса 2");
        var other = Create("Чужая касса", code: "другой код");

        var milk = first.AddSupplier("Молочный завод");
        first.RecordDelivery(milk, 67234, 70000, "");
        Sync();

        var seen = Assert.Single(second.Suppliers);
        Assert.Equal("Молочный завод", seen.Name);
        Assert.Equal(2766, seen.Balance);
        Assert.Empty(other.Suppliers); // другой код — ничего не видно

        // Записи с обоих компьютеров, в том числе отмена чужой записи.
        second.RecordReturn(seen, 5000, "");
        second.Cancel(seen.Entries[0], "не та накладная");
        first.RecordPayout(milk, 1000, "");
        Sync();

        Assert.Equal(5000 - 1000, milk.Balance);
        Assert.Equal(milk.Balance, seen.Balance);
        Assert.All(new[] { first, second }, s => Assert.All(s.Peers, p => Assert.True(p.InSync)));
    }

    [Fact]
    public void Lost_packets_and_late_computers_catch_up()
    {
        var first = Create("Касса 1");
        var milk = first.AddSupplier("Молочный завод");
        for (var i = 0; i < 60; i++) first.RecordDelivery(milk, 10000, 10100, $"накладная {i}");

        // Второй компьютер включили позже, и сеть теряет каждый третий пакет.
        _network.DropEvery = 3;
        var second = Create("Касса 2");
        var third = Create("Касса 3");
        Sync(rounds: 10);

        Assert.All(new[] { second, third }, s => Assert.Equal(6000, Assert.Single(s.Suppliers).Balance));
    }

    // ---- Сайт поставщика ----

    private sealed class FakeSite : ISupplierSite
    {
        public List<ExternalOrder> Orders { get; } = new();
        public List<ExternalReturn> Returns { get; } = new();
        public string? Error { get; set; }
        public (string Login, string Password, DateTime Since)? Last { get; private set; }

        public Task<SiteData> FetchAsync(string login, string password, DateTime since, CancellationToken cancel, SiteDebug? debug = null)
        {
            Last = (login, password, since);
            if (Error != null) throw new SiteException(Error);
            return Task.FromResult(new SiteData(Orders.ToList(), Returns.ToList()));
        }
    }

    [Fact]
    public async Task Site_orders_wait_for_payment_and_credited_returns_go_to_balance()
    {
        var site = new FakeSite();
        var today = DateTime.Today;
        site.Orders.Add(new ExternalOrder("50000011", today.AddHours(13), "Выдан", 1250000));
        site.Orders.Add(new ExternalOrder("50000012", today.AddHours(11), "Выдан", 979980));
        site.Orders.Add(new ExternalOrder("50000013", today.AddHours(9), "Аннулирован", 0));
        site.Orders.Add(new ExternalOrder("50000014", today.AddDays(-3), "Выдан", 50000)); // до начала учёта
        site.Returns.Add(new ExternalReturn("60000011", today, "Поставлен на баланс", "Отказ клиента", "0", 123450));
        site.Returns.Add(new ExternalReturn("60000012", today, "Согласование", "Отказ клиента", "0", 99900));
        site.Returns.Add(new ExternalReturn("60000013", today.AddDays(-5), "Поставлен на баланс", "Отказ клиента", "0", 10000));

        var cash = Create("Касса", site: site);
        var forum = cash.AddSupplier("Форум-Авто");
        cash.SetBalance(forum, 500000, "начинаем учёт");            // корректировка: было 5000 ₽
        cash.Link(forum, "shop-1", "пароль!", today);
        await cash.RefreshSitesAsync();

        Assert.Equal(("shop-1", "пароль!", today), site.Last);
        Assert.Equal("пароль!", cash.PasswordOf(forum));
        Assert.DoesNotContain("пароль!", forum.Link!.Secret); // в журнале — только зашифрованный
        // Ожидают оплаты два заказа за сегодня; аннулированный и старый — нет.
        Assert.Equal(new[] { "50000012", "50000011" }, forum.PendingOrders.Select(o => o.Number));
        // Возврат «Поставлен на баланс» — сразу плюс к балансу; на согласовании и старый — нет.
        Assert.Equal(500000 + 123450, forum.Balance);

        // Оплатили заказ: по накладной 9 799,80, выдали 9 800 — он больше не ждёт.
        var order = forum.PendingOrders[0];
        cash.RecordDelivery(forum, order.Sum, 980000, "", order.External);
        Assert.Equal(500000 + 123450 + 20, forum.Balance);
        cash.SkipOrder(forum, forum.PendingOrders[0], "оплачен картой");
        Assert.Empty(forum.PendingOrders);

        // Повторное обновление не учитывает возврат второй раз.
        await cash.RefreshSitesAsync();
        Assert.Equal(500000 + 123450 + 20, forum.Balance);
        Assert.Empty(forum.PendingOrders);

        // Отменили запись об оплате — заказ снова ждёт.
        cash.Cancel(forum.Entries.Single(e => e.Operation.Kind == CashKind.Delivery), "не тот заказ");
        Assert.Single(forum.PendingOrders);
    }

    [Fact]
    public async Task Same_return_from_two_computers_counts_once()
    {
        var site = new FakeSite();
        var first = Create("Касса 1", site: site);
        var second = Create("Касса 2", site: site);
        var forum = first.AddSupplier("Форум-Авто");
        first.Link(forum, "shop-1", "secret", DateTime.Today);
        Sync();
        var seen = Assert.Single(second.Suppliers);
        Assert.Equal("secret", second.PasswordOf(seen)); // тот же код кассы — пароль расшифровывается и там

        // Возврат появился, когда связи между кассами не было: обе записали его.
        site.Returns.Add(new ExternalReturn("60000011", DateTime.Today, "Поставлен на баланс", "Отказ клиента", "0", 123450));
        _network.Offline = true;
        await first.RefreshSitesAsync();
        await second.RefreshSitesAsync();
        _network.Offline = false;
        Sync();

        Assert.Equal(123450, forum.Balance);
        Assert.Equal(123450, seen.Balance);
        Assert.Single(forum.Entries, e => e.IsDuplicate);
    }

    [Fact]
    public async Task Site_error_is_shown_to_the_cashier()
    {
        var site = new FakeSite { Error = "Сайт не пустил: проверьте логин и пароль" };
        var cash = Create("Касса", site: site);
        var forum = cash.AddSupplier("Форум-Авто");
        cash.Link(forum, "shop-1", "wrong", DateTime.Today);
        await cash.RefreshSitesAsync();
        Assert.Contains("проверьте логин и пароль", forum.SiteStatus);
    }

    [Fact]
    public void Balance_correction_sets_the_balance_from_that_moment()
    {
        var cash = Create("Касса");
        var milk = cash.AddSupplier("Молочный завод");
        cash.RecordReturn(milk, 10000, "");
        cash.SetBalance(milk, 250000, "сверка с поставщиком");
        cash.RecordDelivery(milk, 67234, 70000, "");
        Assert.Equal(250000 + 2766, milk.Balance);
        Assert.Equal(240000, milk.Entries[1].Delta);
    }

    [Fact]
    public void Forum_auto_pages_are_parsed()
    {
        const string archive = """
            <p>Страниц(139):&nbsp;<input type="text" name="page" value="1"></p>
            <h1>Архив заказов</h1><table><tbody>
            <tr><td class="th">Заказ</td><td class="th">Дата</td><td class="th">Статус заказа</td><td class="th">Сумма</td></tr>
            <tr class="w"><td class="td" align="center"><a href="https://itrade.forum-auto.ru/shop/paid_view.html?paid=1001">50000001</a></td><td class="td" align="center">20.11.2026 13:08:02</td><td class="td">Выдан</td><td class="td">12500.00</td><td class="td">MSK</td></tr>
            <tr class="w"><td class="td" align="center"><a href="https://itrade.forum-auto.ru/shop/paid_view.html?paid=1002">50000002</a></td><td class="td" align="center">17.11.2026 14:22:06</td><td class="td">Аннулирован</td><td class="td">0.00</td></tr>
            </tbody></table><table><tbody>
            <tr class="w"><td class="td" align="center"><a href="https://itrade.forum-auto.ru/shop/paid_view.html?did=50000003">50000003</a> </td><td class="td">17.11.2026 21:52:08</td><td class="td">Выдан</td><td class="td">0</td></tr>
            </tbody></table>
            """;
        var orders = ForumAutoParser.ParseOrders(archive);
        Assert.Equal(2, orders.Count); // документ без заказа (did=…) пропущен
        Assert.Equal(new ExternalOrder("50000001", new DateTime(2026, 11, 20, 13, 8, 2), "Выдан", 1250000), orders[0]);
        Assert.Equal(139, ForumAutoParser.PageCount(archive));
        Assert.False(ForumAutoParser.IsLoginPage(archive));

        const string returns = """
            <h1>Возвраты</h1><table><tbody><tr><td class="th">Ключ</td></tr>
            <tr class="w">    <td class="td"><a href="https://itrade.forum-auto.ru/shop/returns/60000001.html">60000001</a></td>    <td class="td">60000001</td>    <td class="td">20.11.2026</td>    <td class="td">Imsync Imsync</td>    <td class="td">Поставлен на баланс</td>    <td class="td">Отказ клиента</td>    <td class="td">0</td>    <td class="td">1234.5</td></tr>
            <tr class="w">    <td class="td"><a href="https://itrade.forum-auto.ru/shop/returns/60000002.html">60000002</a></td>    <td class="td">28 Ч</td>    <td class="td">04.12.2018</td>    <td class="td">Неизвестно </td>    <td class="td">Аннулирован</td>    <td class="td">Отказ клиента</td>    <td class="td">20</td>    <td class="td">999.99</td></tr>
            </tbody></table>
            """;
        var parsed = ForumAutoParser.ParseReturns(returns);
        Assert.Equal(2, parsed.Count);
        Assert.Equal(new ExternalReturn("60000001", new DateTime(2026, 11, 20), "Поставлен на баланс", "Отказ клиента", "0", 123450), parsed[0]);
        Assert.True(CashService.IsCreditedReturn(parsed[0]));
        Assert.False(CashService.IsCreditedReturn(parsed[1]));

        const string login = """<form><input type="hidden" name="csrf_token" value="abc123"><input type="password" name="password"></form>""";
        Assert.True(ForumAutoParser.IsLoginPage(login));
        Assert.Equal("abc123", ForumAutoParser.CsrfToken(login));
    }

    [Fact]
    public void Forum_auto_parser_tolerates_raw_markup()
    {
        // Как может отдавать сервер: заглавные теги, одинарные кавычки, незакрытые ячейки и строки, <br> в дате.
        const string archive = """
            <H1>Архив заказов</H1><TABLE>
            <TR class='w' onmouseover="hl(this)"><TD class=td align=center><A HREF='paid_view.html?paid=1001'>50000021</A>
            <TD class=td>21.11.2026<br>
            09:15:00<TD class=td>Выдан<TD class=td>15&nbsp;846.40<TD class=td>MSK
            <TR class='w'><TD><a href="/shop/paid_view.html?paid=1002">50000022</a><TD>20.11.2026 23:51<TD>Собран<TD>118.4
            </TABLE>
            """;
        var orders = ForumAutoParser.ParseOrders(archive);
        Assert.Equal(new[]
        {
            new ExternalOrder("50000021", new DateTime(2026, 11, 21, 9, 15, 0), "Выдан", 1584640),
            new ExternalOrder("50000022", new DateTime(2026, 11, 20, 23, 51, 0), "Собран", 11840),
        }, orders);
    }

    [Fact]
    public async Task Manual_delivery_closes_the_matching_site_order()
    {
        var site = new FakeSite();
        var cash = Create("Касса", site: site);
        var forum = cash.AddSupplier("Форум-Авто");
        // Поставку записали вручную, а заказ с сайта подтянулся позже — второй раз его не ждём.
        cash.RecordDelivery(forum, 1584640, 1560000, "");
        site.Orders.Add(new ExternalOrder("50000021", DateTime.Now.AddHours(-2), "Выдан", 1584640));
        site.Orders.Add(new ExternalOrder("50000022", DateTime.Now.AddHours(-1), "Выдан", 1584640)); // та же сумма — но ручная одна
        cash.Link(forum, "shop-1", "secret", DateTime.Today);
        await cash.RefreshSitesAsync();
        Assert.Equal(new[] { "50000022" }, forum.PendingOrders.Select(o => o.Number));
    }

    // ---- Сеть в памяти ----

    [Fact]
    public void Site_report_preserves_parse_errors_and_hides_credentials()
    {
        var debug = new SiteDebug();
        debug.HideSecret("test-password-123");
        debug.Line("error: test-password-123");
        debug.AddHtml("response.html", """
            <meta charset="windows-1251"><input value='csrf-secret-456' name='csrf_token'>
            <input name="password" value="test-password-123"><p>Архив заказов</p>
            """);
        ForumAutoParser.ParseOrders("""
            <table><tr><td><a href="paid_view.html?paid=1">12345</a></td>
            <td>21.11.2026</td><td>Выдан</td><td>не число</td></tr></table>
            """, debug);
        var path = debug.Save();
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            var contents = zip.Entries.Select(e =>
            {
                using var reader = new StreamReader(e.Open());
                return reader.ReadToEnd();
            }).ToArray();
            var text = string.Join("\n", contents);
            Assert.DoesNotContain("test-password-123", text);
            Assert.DoesNotContain("csrf-secret-456", text);
            Assert.Contains("Архив заказов", text);
            Assert.Contains("не читается", text);
            Assert.Contains("charset=\"utf-8\"", text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Site_report_explains_rejected_returns_and_pending_orders()
    {
        var site = new FakeSite();
        site.Orders.Add(new ExternalOrder("123", DateTime.Today, "Выдан", 10000));
        site.Returns.Add(new ExternalReturn("456", DateTime.Today, "Аннулирован", "", "0", 10000));
        var cash = Create("Диагностика", site: site);
        var supplier = cash.AddSupplier("Форум");
        cash.Link(supplier, "login", "password", DateTime.Today);
        var path = await cash.DebugSiteAsync(supplier);
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            using var reader = new StreamReader(zip.GetEntry("report.txt")!.Open());
            var report = reader.ReadToEnd();
            Assert.Contains("статус не «на баланс»", report);
            Assert.Contains("ЖДЁТ ОПЛАТЫ", report);
            Assert.Single(supplier.PendingOrders);
            Assert.Equal(0, supplier.Balance);
        }
        finally { File.Delete(path); }
    }

    private sealed class FakeNetwork
    {
        private readonly List<FakeTransport> _members = new();
        private readonly Queue<Action> _queue = new();
        private int _counter;

        public int DropEvery { get; set; }
        /// <summary>Сеть «пропала»: пакеты никуда не доходят.</summary>
        public bool Offline { get; set; }
        public SynchronizationContext Context { get; }

        public FakeNetwork() => Context = new QueueContext(_queue);

        public ICashTransport Join()
        {
            var transport = new FakeTransport(this, new IPEndPoint(IPAddress.Loopback, 10000 + _members.Count));
            _members.Add(transport);
            return transport;
        }

        public void Deliver(byte[] data, FakeTransport from, FakeTransport to)
        {
            if (Offline || DropEvery > 0 && ++_counter % DropEvery == 0) return;
            to.Raise(data, from.EndPoint);
        }

        public void Broadcast(byte[] data, FakeTransport from)
        {
            foreach (var member in _members.Where(m => m != from && !m.Disposed)) Deliver(data, from, member);
        }

        public void Send(byte[] data, FakeTransport from, IPEndPoint to)
        {
            if (_members.FirstOrDefault(m => m.EndPoint.Equals(to) && !m.Disposed) is { } target) Deliver(data, from, target);
        }

        public void Pump()
        {
            for (var i = 0; _queue.Count > 0 && i < 100_000; i++) _queue.Dequeue()();
        }
    }

    private sealed class QueueContext(Queue<Action> queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => queue.Enqueue(() => d(state));
    }

    private sealed class FakeTransport(FakeNetwork network, IPEndPoint endPoint) : ICashTransport
    {
        public IPEndPoint EndPoint { get; } = endPoint;
        public bool Disposed { get; private set; }
        public event Action<byte[], IPEndPoint>? Received;
        public void Start() { }
        public void Broadcast(byte[] data) => network.Broadcast(data, this);
        public void Send(byte[] data, IPEndPoint to) => network.Send(data, this, to);
        public void Raise(byte[] data, IPEndPoint from) => Received?.Invoke(data, from);
        public void Dispose() => Disposed = true;
    }
}
