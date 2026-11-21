using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OfficeChat.Models;

namespace OfficeChat.Services;

public static class SupplierSites
{
    public static string Title(string id) => id switch
    {
        ForumAutoSite.Id => "Форум-Авто",
        FavoritSite.Id => "Фаворит (наличные)",
        _ => "Неизвестный сайт",
    };

    public static ISupplierSite Create(string id) => id switch
    {
        ForumAutoSite.Id => new ForumAutoSite(),
        FavoritSite.Id => new FavoritSite(),
        _ => throw new SiteException($"Неизвестный сайт поставщика: {id}. Обновите приложение."),
    };
}

/// <summary>
/// API, которым пользуется страница /finance/: POST /rest/user/auth/ и GET /rest/finance/.
/// HTML-оболочка React сама не содержит финансовых строк. Cookies живут только в рамках загрузки.
/// </summary>
public sealed class FavoritSite : ISupplierSite
{
    public const string Id = "favorit";
    private const string BaseUrl = "https://lk.favorit-parts.ru";
    private readonly Func<HttpMessageHandler>? _handlerFactory;

    public FavoritSite(Func<HttpMessageHandler>? handlerFactory = null) => _handlerFactory = handlerFactory;

    public async Task<SiteData> FetchAsync(string login, string password, DateTime since, CancellationToken cancel,
        SiteDebug? debug = null)
    {
        debug?.HideSecret(password);
        debug?.HideSecret(WebUtility.HtmlEncode(password));
        debug?.HideSecret(WebUtility.UrlEncode(password));
        var cookies = new CookieContainer();
        using var handler = _handlerFactory?.Invoke() ?? new HttpClientHandler
        {
            CookieContainer = cookies, AutomaticDecompression = DecompressionMethods.All,
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Add("Content-Customer", "Favorit");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 OfficeChat-Cash");
        debug?.Line($"Фаворит: наличный договор, учёт с {since:O}; логин «{login}»");
        async Task<string> Request(HttpRequestMessage request, string name)
        {
            using (request)
            {
                debug?.Line($"{request.Method} {request.RequestUri}");
                var started = System.Diagnostics.Stopwatch.StartNew();
                using var response = await http.SendAsync(request, cancel);
                var body = await response.Content.ReadAsStringAsync(cancel);
                foreach (Cookie cookie in cookies.GetAllCookies()) debug?.HideSecret(cookie.Value);
                debug?.Line($"  HTTP {(int)response.StatusCode}, адрес {response.RequestMessage?.RequestUri}, " +
                            $"Content-Type {response.Content.Headers.ContentType}, символов {body.Length}, {started.ElapsedMilliseconds} мс");
                if (debug != null)
                {
                    if (body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[')) debug.AddJson(name + ".json", body);
                    else debug.AddHtml(name + ".html", body);
                }
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new SiteException("Фаворит не пустил: проверьте логин и пароль / права доступа");
                if ((int)response.StatusCode == 423) throw new SiteException("Фаворит: учётная запись заблокирована");
                response.EnsureSuccessStatusCode();
                return body;
            }
        }
        try
        {
            await Request(new HttpRequestMessage(HttpMethod.Get, "/finance/"), "finance-shell");
            await Request(new HttpRequestMessage(HttpMethod.Post, "/rest/user/auth/")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["login"] = login, ["password"] = password }),
            }, "auth-response");
            var json = await Request(new HttpRequestMessage(HttpMethod.Get,
                $"/rest/finance/?datestart={since:yyyyMMdd}&dateend={DateTime.Today:yyyyMMdd}"), "finance");
            return FavoritParser.ParseJson(json, since, debug);
        }
        catch (Exception ex)
        {
            debug?.Line($"Фаворит: ОШИБКА {ex}");
            if (ex is HttpRequestException) throw new SiteException("Фаворит недоступен: " + ex.Message, ex);
            if (ex is OperationCanceledException && !cancel.IsCancellationRequested)
                throw new SiteException("Фаворит не ответил вовремя", ex);
            throw;
        }
    }
}

public static class FavoritParser
{
    private sealed record Row(string Id, string Type, string Name, string Date, string Income, string Outcome);
    private static bool IsCash(string name) => Regex.IsMatch(name, @"Договор\s*№\s*б\s*/\s*н\b", RegexOptions.IgnoreCase);
    private static string Text(string html) => Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]*>", " ")), @"\s+", " ").Trim();

    public static SiteData ParseJson(string json, DateTime since, SiteDebug? debug = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new SiteException("Фаворит: ожидался список договоров; сохранён ответ для диагностики");
            var contracts = doc.RootElement.EnumerateObject().ToList();
            debug?.Line("Договоры в ответе: " + string.Join(" | ", contracts.Select(c => c.Name)));
            var cash = contracts.Where(c => IsCash(c.Name)).ToList();
            if (cash.Count != 1) throw new SiteException($"Фаворит: найдено наличных договоров б/н: {cash.Count}, нужен один. Проверьте отчёт.");
            var contract = cash[0];
            var balance = Value(contract.Value.GetProperty("header"), "endSum");
            var rows = contract.Value.GetProperty("row");
            if (rows.ValueKind != JsonValueKind.Array) throw new SiteException("Фаворит: неизвестный формат строк договора");
            return Parse(contract.Name, balance, rows.EnumerateArray().Select(r => new Row(
                Value(r, "id"), Value(r, "type"), Value(r, "name"), Value(r, "date"), Value(r, "income"), Value(r, "outcome"))), since, debug);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new SiteException("Фаворит: ответ не похож на отчёт взаиморасчётов (возможно, форма входа). Подробности в диагностическом ZIP.", ex);
        }
    }

    private static string Value(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText() : "";

    /// <summary>Тот же разбор для сохранённой браузером страницы, включая свёрнутые ReactCollapse-блоки.</summary>
    public static SiteData ParseHtml(string html, DateTime since, SiteDebug? debug = null)
    {
        var headers = Regex.Matches(html, "<div\\s+class=\"financeFlex financeFlexDocument\"[^>]*>");
        var blocks = headers.Select((m, i) => html[m.Index..(i + 1 < headers.Count ? headers[i + 1].Index : html.Length)]).ToList();
        static List<string> Cells(string block, string cls) => Regex.Matches(block,
            "<div\\s+class=\"" + cls + "\"[^>]*>(.*?)</div>", RegexOptions.Singleline).Select(m => Text(m.Groups[1].Value)).ToList();
        var cash = blocks.Where(b => Cells(b, "financeDocument").FirstOrDefault() is { } name && IsCash(name)).ToList();
        if (cash.Count != 1) throw new SiteException("Фаворит: в HTML не найден единственный наличный договор б/н");
        var header = Cells(cash[0], "financeDocument");
        if (header.Count != 5) throw new SiteException("Фаворит: не удалось разобрать итог наличного договора");
        var matches = Regex.Matches(cash[0], "<div\\s+class=\"financeFlex financeFlexItem\"[^>]*>");
        var rows = new List<Row>();
        for (var i = 0; i < matches.Count; i++)
        {
            var block = cash[0][matches[i].Index..(i + 1 < matches.Count ? matches[i + 1].Index : cash[0].Length)];
            var cells = Cells(block, "financeDocumentItem(?:-sum)?");
            if (cells.Count != 7) throw new SiteException($"Фаворит: строка HTML {i + 1}, ячеек {cells.Count} вместо 7");
            var link = Regex.Match(block, @"/(invoice|refundprint)/([^/""']+)/");
            var type = link.Groups[1].Value switch { "invoice" => "invoice", "refundprint" => "refund", _ => "payment" };
            rows.Add(new Row(link.Success ? link.Groups[2].Value : cells[2], type, cells[2], cells[0], cells[4], cells[5]));
        }
        return Parse(header[0], header[4], rows, since, debug);
    }

    private static SiteData Parse(string contract, string endSum, IEnumerable<Row> rows, DateTime since, SiteDebug? debug)
    {
        var debt = Money.ParseSigned(endSum) ?? throw new SiteException("Фаворит: конечный остаток наличного договора не читается");
        var orders = new List<ExternalOrder>();
        var returns = new List<ExternalReturn>();
        var payments = new List<ExternalPayment>();
        debug?.Line($"Выбран {contract}; долг сайта {Money.Format(debt)}, баланс кассы для сравнения {Money.Format(-debt)}. Другие договоры исключены.");
        var index = 0;
        foreach (var row in rows)
        {
            index++;
            debug?.Line($"Строка {index}: id={row.Id}, type={row.Type}, {row.Date}, {row.Name}; отгрузка={row.Income}, оплата={row.Outcome}");
            var dateMatch = Regex.Match(row.Name, @"\b(\d{2}\.\d{2}\.\d{4})(?:\s+(\d{1,2}:\d{2}:\d{2}))?");
            var dateText = dateMatch.Success ? dateMatch.Value : row.Date;
            if (!DateTime.TryParse(dateText, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.None, out var date))
                throw new SiteException($"Фаворит: не читается дата строки {index}");
            if (date < since) { debug?.Line("  пропущена: раньше начала учёта"); continue; }
            var income = Money.ParseSigned(row.Income);
            var outcome = Money.ParseSigned(row.Outcome);
            if (income == null || outcome == null) throw new SiteException($"Фаворит: не читается сумма строки {index}");
            var number = Regex.Replace(row.Name, @"^(Расходная накладная|Возврат от покупателя|Приходный кассовый ордер)\s+", "");
            number = Regex.Split(number, @"\s+от\s+\d{2}\.")[0];
            if (row.Type is "invoice" or "refund")
            {
                if (string.IsNullOrWhiteSpace(row.Id) || row.Id.Length > 75)
                    throw new SiteException($"Фаворит: неверный идентификатор документа строки {index}");
                var key = row.Id.ToLowerInvariant();
                if (row.Type == "invoice" && income > 0)
                    orders.Add(new ExternalOrder(number, date, "Отгружен", income.Value) { ExternalKey = "favorit:order:" + key });
                else if (row.Type == "refund" && outcome > 0)
                    returns.Add(new ExternalReturn(number, date, "Поставлен на баланс", row.Name, "0", outcome.Value) { ExternalKey = "favorit:return:" + key });
                else debug?.Line("  пропущена: сумма документа не положительная");
            }
            else if (row.Name.StartsWith("Приходный кассовый ордер", StringComparison.OrdinalIgnoreCase) && outcome > 0)
            {
                payments.Add(new ExternalPayment(number, date, outcome.Value));
                debug?.Line("  оплата для сверки; не дублируем её в журнале кассы");
            }
            else debug?.Line("  пропущена: тип документа не относится к отгрузке, возврату или наличной оплате");
        }
        debug?.Line($"ИТОГ Фаворит: строк {index}, отгрузок {orders.Count}, возвратов {returns.Count}, оплат для сверки {payments.Count}");
        return new SiteData(orders.DistinctBy(o => o.External).ToList(), returns.DistinctBy(r => r.External).ToList())
        {
            Balance = -debt, BalanceSource = contract, Payments = payments.OrderByDescending(p => p.Date).ToList(),
        };
    }
}
