using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OfficeChat.Models;

namespace OfficeChat.Services;

/// <summary>Запросы страницы /account/orders: отдельная сессия, без доступа к cookies браузера.</summary>
public sealed class AutorusSite(Func<HttpMessageHandler>? handlerFactory = null) : ISupplierSite
{
    public const string Id = "autorus";

    public async Task<SiteData> FetchAsync(string login, string password, DateTime since,
        CancellationToken cancel, SiteDebug? debug = null)
    {
        debug?.HideSecret(password);
        debug?.HideSecret(WebUtility.HtmlEncode(password));
        debug?.HideSecret(WebUtility.UrlEncode(password));
        var cookies = new CookieContainer();
        using var handler = handlerFactory?.Invoke() ?? new HttpClientHandler
        {
            CookieContainer = cookies, AutomaticDecompression = DecompressionMethods.All,
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://www.autorus.ru"), Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 OfficeChat-Cash");
        http.DefaultRequestHeaders.Referrer = new Uri("https://www.autorus.ru/account/orders");
        http.DefaultRequestHeaders.Add("Origin", "https://www.autorus.ru");
        async Task<string> Request(string url, object? payload, string name)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            if (payload != null) request.Content = JsonContent.Create(payload);
            debug?.Line($"POST {url}");
            using var response = await http.SendAsync(request, cancel);
            var body = await response.Content.ReadAsStringAsync(cancel);
            foreach (Cookie cookie in cookies.GetAllCookies()) debug?.HideSecret(cookie.Value);
            debug?.Line($"HTTP {(int)response.StatusCode}; Content-Type {response.Content.Headers.ContentType}; символов {body.Length}");
            if (body.TrimStart().StartsWith('{')) debug?.AddJson(name + ".json", body);
            else debug?.AddHtml(name + ".html", body);
            if (!response.IsSuccessStatusCode)
                throw new SiteException($"Авторусь: HTTP {(int)response.StatusCode}. Проверьте вход по логину и паролю; сайт может требовать дополнительную проверку. Подробности в отладке.");
            return body;
        }
        try
        {
            var auth = await Request("/gw/passport/spa/v1/profile/login", new { login, password }, "auth");
            using (var doc = JsonDocument.Parse(auth))
            {
                if (!doc.RootElement.TryGetProperty("data", out var user) ||
                    !user.TryGetProperty("isAuthorized", out var authorized) || authorized.ValueKind != JsonValueKind.True)
                    throw new SiteException("Авторусь не подтвердил вход. Проверьте логин и пароль; ответ сохранён в отладке.");
            }
            var scan = new AutorusScan(since, debug);
            for (var page = 1; page <= 200; page++)
            {
                cancel.ThrowIfCancellationRequested();
                var json = await Request($"/gw/orders/v3/user-order/list?page={page}", null, $"orders-{page:000}");
                if (scan.AddPage(json, page)) return new SiteData(scan.Orders, new());
            }
            throw new SiteException("Авторусь: достигнут предел 200 страниц; неполная загрузка не применена.");
        }
        catch (JsonException ex) { throw new SiteException("Авторусь вернул неожиданный ответ вместо JSON. См. файл отладки.", ex); }
        catch (HttpRequestException ex) { throw new SiteException("Авторусь недоступен: " + ex.Message, ex); }
        catch (OperationCanceledException ex) when (!cancel.IsCancellationRequested)
        { throw new SiteException("Авторусь не ответил вовремя", ex); }
    }
}

/// <summary>Счётчик относится к заказам, а не товарам внутри заказа, и сохраняется между страницами.</summary>
public sealed class AutorusScan(DateTime since, SiteDebug? debug = null)
{
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private int _received;
    public List<ExternalOrder> Orders { get; } = new();

    public bool AddPage(string json, int page)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");
            var rows = data.GetProperty("orders");
            if (rows.ValueKind != JsonValueKind.Array) throw new FormatException("orders не массив");
            var pagination = data.GetProperty("pagination");
            var actualPage = Int(pagination.GetProperty("page"));
            var limit = Int(pagination.GetProperty("limit"));
            var total = Int(pagination.GetProperty("total"));
            if (actualPage != page || limit <= 0 || total < 0 || rows.GetArrayLength() > limit)
                throw new FormatException("неверная пагинация");
            debug?.Line($"Авторусь: страница {page}, строк {rows.GetArrayLength()}, всего {total}, серия полученных перед страницей {_received}");
            if (rows.GetArrayLength() == 0 && (long)(page - 1) * limit < total)
                throw new FormatException("пустая страница при ненулевом остатке");
            foreach (var row in rows.EnumerateArray())
            {
                var number = row.GetProperty("orderNumber").ToString().Trim();
                var status = row.GetProperty("statusTitle").GetString()?.Trim() ?? "";
                var dateText = row.GetProperty("orderCreated").GetString();
                var sum = Money.Parse(row.GetProperty("total").ToString());
                if (number.Length is 0 or > 70 || status.Length == 0 || sum == null ||
                    !DateTime.TryParseExact(dateText, "dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    throw new FormatException($"не удалось разобрать заказ {number}: дата, сумма или статус");
                if (!_seen.Add(number)) throw new FormatException($"повтор заказа {number}; страницы сместились, повторите обновление");
                _received = status.Equals("Получен", StringComparison.OrdinalIgnoreCase) ? _received + 1 : 0;
                if (date >= since && sum > 0)
                    Orders.Add(new ExternalOrder(number, date, status, sum.Value) { ExternalKey = "autorus:order:" + number });
                debug?.Line($"Заказ {number}: {status}, {date:O}, {sum} коп.; серия {_received}; {(date >= since ? "в периоде" : "раньше начала учёта")}");
                if (_received >= 10)
                {
                    debug?.Line("ОСТАНОВКА: 10 заказов подряд со статусом «Получен» (включая границу страниц).");
                    return true;
                }
            }
            if ((long)page * limit >= total)
            {
                debug?.Line("ОСТАНОВКА: последняя страница по данным сайта.");
                return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            debug?.Line($"Авторусь: страница {page} не разобрана: {ex.Message}");
            throw new SiteException($"Авторусь: не удалось разобрать страницу {page}: {ex.Message}. Неполная загрузка не применена; см. отладку.", ex);
        }
    }

    private static int Int(JsonElement value) => int.Parse(value.ToString(), CultureInfo.InvariantCulture);
}
