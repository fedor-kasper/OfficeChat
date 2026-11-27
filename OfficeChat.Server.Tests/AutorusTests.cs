using System.Net;
using System.Text;
using System.Text.Json;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Server.Tests;

public class AutorusTests
{
    // Имена полей и пагинация взяты из публичных скриптов страницы; все значения вымышлены.
    private static string Page(int page, params string[] statuses) => JsonSerializer.Serialize(new
    {
        data = new
        {
            orders = statuses.Select((status, i) => new
            {
                orderNumber = ((page - 1) * 10 + i + 1).ToString(), orderCreated = "23.11.2026 13:52:45",
                statusTitle = status, total = 172.02m,
            }),
            pagination = new { page, limit = 10, total = 30 },
        },
    });

    [Fact]
    public void Ten_received_across_pages_stop_before_remaining_orders()
    {
        var scan = new AutorusScan(new DateTime(2026, 11, 1));
        Assert.False(scan.AddPage(Page(1, "Готов", "Готов", "Готов", "Получен", "Получен", "Получен", "Получен", "Получен", "Получен", "Получен"), 1));
        Assert.True(scan.AddPage(Page(2, Enumerable.Repeat("Получен", 10).ToArray()), 2));
        Assert.Equal(13, scan.Orders.Count);
        Assert.Equal("autorus:order:13", scan.Orders[^1].External);
        Assert.All(scan.Orders, o => Assert.Equal(17202, o.Sum));
    }

    [Fact]
    public void Non_received_status_resets_series_and_old_orders_still_count()
    {
        var scan = new AutorusScan(new DateTime(2026, 11, 24));
        Assert.False(scan.AddPage(Page(1, "Получен", "Получен", "Получен", "Получен", "Получен", "Получен", "Получен", "Получен", "Получен", "Готов"), 1));
        Assert.False(scan.AddPage(Page(2, "Получен", "Получен", "Получен", "В работе", "Получен", "Получен", "Получен", "Получен", "Получен", "Получен"), 2));
        Assert.True(scan.AddPage(Page(3, Enumerable.Repeat("Получен", 10).ToArray()), 3));
        Assert.Empty(scan.Orders);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("<html>Войти</html>")]
    [InlineData("{\"data\":{\"orders\":[],\"pagination\":{\"page\":1,\"limit\":10,\"total\":20}}}")]
    public void Invalid_or_unexpected_empty_response_fails(string json) =>
        Assert.Throws<SiteException>(() => new AutorusScan(DateTime.MinValue).AddPage(json, 1));

    [Fact]
    public void Repeated_page_and_broken_amount_fail_instead_of_importing_partial_data()
    {
        var scan = new AutorusScan(DateTime.MinValue);
        var page = Page(1, "Готов");
        Assert.False(scan.AddPage(page, 1));
        Assert.Throws<SiteException>(() => scan.AddPage(page.Replace("\"page\":1", "\"page\":2"), 2));
        Assert.Throws<SiteException>(() => new AutorusScan(DateTime.MinValue).AddPage(page.Replace("172.02", "172.023"), 1));
    }

    [Fact]
    public void Empty_account_is_valid_only_with_matching_pagination()
    {
        Assert.True(new AutorusScan(DateTime.MinValue).AddPage(
            "{\"data\":{\"orders\":[],\"pagination\":{\"page\":1,\"limit\":10,\"total\":0}}}", 1));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<string>> response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            new(HttpStatusCode.OK) { Content = new StringContent(await response(request), Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task Fetch_logs_in_then_requests_pages_and_stops_at_ten()
    {
        var calls = new List<string>();
        var site = new AutorusSite(() => new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            var path = request.RequestUri!.PathAndQuery;
            calls.Add(path);
            if (calls.Count == 1)
            {
                Assert.Equal("/gw/passport/spa/v1/profile/login", path);
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                Assert.Equal("test-password", json.RootElement.GetProperty("password").GetString());
                return "{\"data\":{\"isAuthorized\":true}}";
            }
            Assert.Equal("/gw/orders/v3/user-order/list?page=1", path);
            return Page(1, Enumerable.Repeat("Получен", 10).ToArray());
        }));
        var result = await site.FetchAsync("test@example.com", "test-password", DateTime.MinValue, CancellationToken.None);
        Assert.Equal(2, calls.Count);
        Assert.Equal(10, result.Orders.Count);
        Assert.Empty(result.Returns);
        Assert.Null(result.Balance);
    }

    [Fact]
    public async Task Failed_login_cannot_be_treated_as_empty_orders()
    {
        var site = new AutorusSite(() => new Handler(_ => Task.FromResult("{\"data\":{\"isAuthorized\":false}}")));
        await Assert.ThrowsAsync<SiteException>(() => site.FetchAsync("test@example.com", "test", DateTime.MinValue, CancellationToken.None));
    }

    [Theory]
    [InlineData("+7 (900) 000-00-00", "79000000000")]
    [InlineData("8 900 000 00 00", "79000000000")]
    [InlineData("9000000000", "79000000000")]
    [InlineData("79000000000", "79000000000")]
    [InlineData(" user@example.com ", "user@example.com")]
    public void Login_is_normalized(string input, string expected) => Assert.Equal(expected, AutorusSite.NormalizeLogin(input));

    [Theory]
    [InlineData("")]
    [InlineData("900")]
    [InlineData("abc79000000000")]
    public void Incomplete_phone_is_rejected(string input) => Assert.Throws<SiteException>(() => AutorusSite.NormalizeLogin(input));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Phone_is_checked_before_password_and_missing_account_stops(bool exists)
    {
        var calls = new List<string>();
        var site = new AutorusSite(() => new Handler(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(path);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (calls.Count == 1)
            {
                Assert.Equal("/gw/passport/spa/v1/profile/exist", path);
                Assert.Equal("79000000000", body.RootElement.GetProperty("phone").GetString());
                Assert.False(body.RootElement.TryGetProperty("password", out _));
                return JsonSerializer.Serialize(new { data = new { exist = exists } });
            }
            Assert.True(exists);
            Assert.Equal("/gw/passport/spa/v1/profile/login", path);
            Assert.Equal("79000000000", body.RootElement.GetProperty("login").GetString());
            Assert.Equal("password", body.RootElement.GetProperty("password").GetString());
            return "{\"data\":{\"isAuthorized\":false}}";
        }));
        var error = await Assert.ThrowsAsync<SiteException>(() => site.FetchAsync("+7 (900) 000-00-00", "password", DateTime.MinValue, CancellationToken.None));
        Assert.Equal(exists ? 2 : 1, calls.Count);
        Assert.Contains(exists ? "не подтвердил вход" : "не найдена", error.Message);
    }

    [Fact]
    public void Payment_method_survives_serialization_and_legacy_data_is_unspecified()
    {
        var operation = new CashOperation { Kind = CashKind.Delivery, Due = 17202, Paid = 17202, PaymentMethod = CashPaymentMethod.Card };
        var restored = JsonSerializer.Deserialize<CashOperation>(JsonSerializer.Serialize(operation))!;
        Assert.Equal(CashPaymentMethod.Card, restored.PaymentMethod);
        Assert.Equal(0, restored.Delta);
        Assert.Contains("карта", new CashEntry { Operation = restored }.Details);
        var legacy = JsonSerializer.Deserialize<CashOperation>("{\"kind\":10,\"due\":17202,\"paid\":20000}")!;
        Assert.Equal(CashPaymentMethod.Unspecified, legacy.PaymentMethod);
        Assert.Contains("не указан", new CashEntry { Operation = legacy }.Details);
    }

    [Fact]
    public void Diagnostic_masks_login_session_and_records_stop_reason()
    {
        var debug = new SiteDebug(AutorusSite.Id);
        debug.AddJson("auth.json", "{\"data\":{\"sessionID\":\"private-session\",\"token\":\"private-token\"}}");
        new AutorusScan(DateTime.MinValue, debug).AddPage(Page(1, Enumerable.Repeat("Получен", 10).ToArray()), 1);
        var path = debug.Save();
        try
        {
            Assert.StartsWith("autorus-debug-", Path.GetFileName(path));
            using var zip = System.IO.Compression.ZipFile.OpenRead(path);
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                var text = reader.ReadToEnd();
                Assert.DoesNotContain("private-session", text);
                Assert.DoesNotContain("private-token", text);
                if (entry.Name == "report.txt") Assert.Contains("10 заказов подряд", text);
            }
        }
        finally { File.Delete(path); }
    }
}
