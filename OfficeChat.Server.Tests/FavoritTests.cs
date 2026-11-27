using System.Net;
using System.Text;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Server.Tests;

public class FavoritTests
{
    // Форма ответа подтверждена app.bundle.js страницы /finance/. Данные примера обезличены.
    private const string Finance = """
        {
          "Договор № БК от 04.04.2022": {"header":{"endSum":9000},"row":[
            {"id":"other","type":"invoice","name":"Расходная накладная БК1 от 21.11.2026 10:00:00","date":"2026-11-21","income":9000,"outcome":0}]},
          "Договор № б/н от 16.06.2010": {"header":{"endSum":43481.52},"row":[
            {"id":"ABCD-1","type":"invoice","name":"Расходная накладная Б21.11/0824 от 21.11.2026 11:02:15","date":"2026-11-21","income":14129.04,"outcome":0},
            {"id":"ABCD-2","type":"refund","name":"Возврат от покупателя 1112266678 от 17.11.2026 15:00:14","date":"2026-11-17","income":0,"outcome":"2 808,00"},
            {"id":"ABCD-3","type":"cash","name":"Приходный кассовый ордер 0000585856 от 19.11.2026 16:44:55","date":"2026-11-19","income":0,"outcome":2200}]},
          "Договор № КП16061 от 20.06.2022": {"header":{"endSum":8000},"row":[]}
        }
        """;

    [Fact]
    public void Cash_contract_is_selected_by_name_not_position_and_other_contracts_are_excluded()
    {
        var data = FavoritParser.ParseJson(Finance, new DateTime(2026, 11, 1));
        Assert.Equal(-4348152, data.Balance);
        var order = Assert.Single(data.Orders);
        Assert.Equal("Б21.11/0824", order.Number);
        Assert.Equal(1412904, order.Sum);
        Assert.Equal("favorit:order:abcd-1", order.External);
        Assert.Equal(new DateTime(2026, 11, 21, 11, 2, 15), order.Date);
        Assert.Equal(280800, Assert.Single(data.Returns).Sum);
        Assert.Equal("favorit:return:abcd-2", data.Returns[0].External);
        Assert.Equal(220000, Assert.Single(data.Payments).Sum);
    }

    [Fact]
    public void Current_debt_cutoff_does_not_reimport_earlier_documents()
    {
        var data = FavoritParser.ParseJson(Finance, new DateTime(2026, 11, 21, 12, 0, 0));
        Assert.Empty(data.Orders);
        Assert.Empty(data.Returns);
        Assert.Empty(data.Payments);
        Assert.Equal(-4348152, data.Balance);
    }

    [Theory]
    [InlineData("<html>Войти</html>")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"Договор № БК\":{\"header\":{\"endSum\":0},\"row\":[]}}")]
    [InlineData("{\"Договор № б/н\":{\"header\":{},\"row\":[]}}")]
    public void Unexpected_response_is_an_error_not_an_empty_success(string response) =>
        Assert.Throws<SiteException>(() => FavoritParser.ParseJson(response, DateTime.Today));

    [Fact]
    public void Collapsed_html_rows_are_read_with_signed_contract_balance()
    {
        const string html = """
            <div class="financeFlex financeFlexDocument"><div class="financeDocument">Договор № б/н от 16.06.2010</div>
            <div class="financeDocument"><span>0,00</span></div><div class="financeDocument">100,00</div>
            <div class="financeDocument">120,00</div><div class="financeDocument">-20,00</div></div>
            <div class="ReactCollapse--collapse" style="height: 0px; overflow: hidden;">
            <div class="financeFlex financeFlexItem"><div class="financeDocumentItem">21.11.2026</div>
            <div class="financeDocumentItem"></div><div class="financeDocumentItem"><a href="/invoice/123/">Расходная накладная Б1 от 21.11.2026 11:02:15</a></div>
            <div class="financeDocumentItem-sum">0,00</div><div class="financeDocumentItem-sum">100,00</div>
            <div class="financeDocumentItem-sum">0,00</div><div class="financeDocumentItem-sum">100,00</div></div></div>
            <div class="financeFlex financeFlexDocument"><div class="financeDocument">Договор № БК</div></div>
            """;
        var data = FavoritParser.ParseHtml(html, new DateTime(2026, 11, 1));
        Assert.Equal(2000, data.Balance);
        Assert.Equal(10000, Assert.Single(data.Orders).Sum);
    }

    [Fact]
    public void Forum_payable_balance_is_not_confused_with_credit_limit()
    {
        const string html = "К оплате: <strong>11800.90 руб.</strong><div>Доступный лимит: <strong>100037.50 руб.</strong></div>";
        Assert.Equal(-1180090, ForumAutoParser.ParseBalance(html));
        Assert.Null(ForumAutoParser.ParseBalance("Доступный лимит: 100037.50 руб."));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request);
    }

    [Fact]
    public async Task Fetch_uses_observed_auth_and_finance_api_and_saves_redacted_json()
    {
        var step = 0;
        var site = new FavoritSite(() => new Handler(async request =>
        {
            Assert.Equal("Favorit", Assert.Single(request.Headers.GetValues("Content-Customer")));
            switch (++step)
            {
                case 1:
                    Assert.Equal("/finance/", request.RequestUri!.AbsolutePath);
                    return new(HttpStatusCode.OK) { Content = new StringContent("<html>React shell</html>") };
                case 2:
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal("/rest/user/auth/", request.RequestUri!.AbsolutePath);
                    var body = await request.Content!.ReadAsStringAsync();
                    Assert.Contains("login=test", body);
                    Assert.Contains("password=secret", body);
                    return new(HttpStatusCode.OK) { Content = new StringContent("{\"token\":\"private-token\",\"ok\":true}", Encoding.UTF8, "application/json") };
                default:
                    Assert.Equal("/rest/finance/", request.RequestUri!.AbsolutePath);
                    Assert.Contains("datestart=20261101", request.RequestUri.Query);
                    return new(HttpStatusCode.OK) { Content = new StringContent(Finance, Encoding.UTF8, "application/json") };
            }
        }));
        var debug = new SiteDebug(FavoritSite.Id);
        var data = await site.FetchAsync("test", "secret", new DateTime(2026, 11, 1), CancellationToken.None, debug);
        Assert.Equal(3, step);
        Assert.Single(data.Orders);
        var path = debug.Save();
        try
        {
            Assert.StartsWith("favorit-debug-", Path.GetFileName(path));
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            Assert.Contains(zip.Entries, e => e.Name.EndsWith("finance.json"));
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var text = reader.ReadToEnd();
                Assert.DoesNotContain("private-token", text);
                Assert.DoesNotContain("secret", text);
            }
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Authentication_failure_does_not_request_finance()
    {
        var calls = 0;
        var site = new FavoritSite(() => new Handler(request => Task.FromResult(new HttpResponseMessage(
            ++calls == 1 ? HttpStatusCode.OK : HttpStatusCode.Unauthorized) { Content = new StringContent("denied") })));
        await Assert.ThrowsAsync<SiteException>(() => site.FetchAsync("test", "secret", DateTime.Today, CancellationToken.None));
        Assert.Equal(2, calls);
    }

    private sealed class Site(Func<string, CancellationToken, Task<SiteData>> fetch) : ISupplierSite
    {
        public Task<SiteData> FetchAsync(string login, string password, DateTime since, CancellationToken cancel, SiteDebug? debug = null) => fetch(login, cancel);
    }

    [Fact]
    public async Task Suppliers_have_independent_balances_and_payments_are_not_double_counted()
    {
        var folder = Path.Combine(Path.GetTempPath(), "officechat-favorit-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings { CashMode = false, CashCode = "test" };
            var favorit = FavoritParser.ParseJson(Finance, new DateTime(2026, 11, 1));
            var site = new Site((login, _) => Task.FromResult(login == "favorit" ? favorit : new SiteData([], []) { Balance = -10000 }));
            using var cash = new CashService(settings, Path.Combine(folder, "cash.db"), site: site) { AutoSync = false };
            var first = cash.AddSupplier("Форум");
            var second = cash.AddSupplier("Фаворит");
            cash.Link(first, "forum", "secret", new DateTime(2026, 11, 1));
            cash.Link(second, "favorit", "secret", new DateTime(2026, 11, 1), FavoritSite.Id);
            settings.CashMode = true;
            await cash.RefreshSitesAsync();
            await cash.RefreshSitesAsync();
            Assert.Equal(ForumAutoSite.Id, first.Link!.Name);
            Assert.Equal(FavoritSite.Id, second.Link!.Name);
            Assert.Equal(-10000, first.SiteBalance);
            Assert.Equal(-4348152, second.SiteBalance);
            Assert.Equal(0, first.Balance);
            Assert.Equal(280800, second.Balance); // Только возврат, без повторного прихода 2200 рублей.
            Assert.Single(second.Entries, e => e.Operation.Kind == CashKind.Return);
            Assert.Single(second.PendingOrders);
            Assert.Single(second.SitePayments);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task Closing_cash_during_fetch_does_not_apply_late_returns_to_closed_database()
    {
        var folder = Path.Combine(Path.GetTempPath(), "officechat-favorit-stop-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings { CashMode = false, CashCode = "test" };
            var result = new TaskCompletionSource<SiteData>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cash = new CashService(settings, Path.Combine(folder, "cash.db"), site: new Site((_, _) => result.Task)) { AutoSync = false };
            cash.Link(cash.AddSupplier("Фаворит"), "favorit", "secret", new DateTime(2026, 11, 1), FavoritSite.Id);
            settings.CashMode = true;
            var refresh = cash.RefreshSitesAsync();
            cash.Dispose();
            result.SetResult(FavoritParser.ParseJson(Finance, new DateTime(2026, 11, 1)));
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Assert.Single(cash.Suppliers).Balance);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Startup_fetches_all_saved_suppliers_immediately_without_twenty_second_delay()
    {
        var folder = Path.Combine(Path.GetTempPath(), "officechat-favorit-start-" + Guid.NewGuid().ToString("N"));
        try
        {
            var settings = new AppSettings { CashMode = false, CashCode = "" };
            var database = Path.Combine(folder, "cash.db");
            using (var setup = new CashService(settings, database) { AutoSync = false })
            {
                setup.Link(setup.AddSupplier("Форум"), "forum", "secret", DateTime.Today);
                setup.Link(setup.AddSupplier("Фаворит"), "favorit", "secret", DateTime.Today, FavoritSite.Id);
            }
            var calls = new List<string>();
            var site = new Site((login, _) =>
            {
                calls.Add(login);
                return Task.FromResult(new SiteData([], []) { Balance = 0 });
            });
            settings.CashMode = true;
            using var cash = new CashService(settings, database, site: site);
            cash.Start();
            Assert.Equal(new[] { "favorit", "forum" }, calls.OrderBy(x => x).ToArray());
            Assert.All(cash.Suppliers, s => Assert.True(s.SiteLoaded));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
