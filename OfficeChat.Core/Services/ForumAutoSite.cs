using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using OfficeChat.Models;

namespace OfficeChat.Services;

/// <summary>Что взяли с сайта поставщика: заказы и возвраты.</summary>
public sealed record SiteData(List<ExternalOrder> Orders, List<ExternalReturn> Returns);

/// <summary>Сайт поставщика, с которого касса сама берёт заказы и возвраты (в тестах — подмена).</summary>
public interface ISupplierSite
{
    /// <summary>
    /// Войти и прочитать заказы и возвраты не раньше <paramref name="since"/>. Ошибка — исключение с понятным текстом.
    /// <paramref name="debug"/> — записывать всё, что пришло с сайта, и как это разобрали (для отладочного отчёта).
    /// </summary>
    Task<SiteData> FetchAsync(string login, string password, DateTime since, CancellationToken cancel, SiteDebug? debug = null);
}

/// <summary>
/// Отладочный отчёт о походе на сайт: каждый запрос и ответ (страница целиком — как пришла), разбор таблиц
/// построчно и итог. Сохраняется одним zip-файлом в папку журналов — его можно переслать разработчику.
/// Пароль и значения cookies в отчёт не попадают.
/// </summary>
public sealed class SiteDebug
{
    private readonly StringBuilder _log = new();
    private readonly List<(string Name, byte[] Data)> _files = new();
    private int _counter;

    public SiteDebug() => Line($"Отладочный отчёт OfficeChat {typeof(SiteDebug).Assembly.GetName().Version}, {DateTime.Now:dd.MM.yyyy HH:mm:ss}, " +
                               $"{Environment.OSVersion}, .NET {Environment.Version}");

    public void Line(string text) => _log.Append($"[{DateTime.Now:HH:mm:ss.fff}] ").AppendLine(text);

    /// <summary>Приложить файл (страницу сайта); имя получает порядковый номер.</summary>
    public string AddFile(string name, byte[] data)
    {
        var file = $"{++_counter:00}-{name}";
        _files.Add((file, data));
        return file;
    }

    /// <summary>Сохранить отчёт: report.txt и приложенные страницы — в zip в папке журналов. Возвращает путь.</summary>
    public string Save()
    {
        Directory.CreateDirectory(Log.Folder);
        var path = Path.Combine(Log.Folder, $"forum-debug-{DateTime.Now:yyyy-MM-dd-HHmmss}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var report = new StreamWriter(zip.CreateEntry("report.txt").Open(), new UTF8Encoding(true)))
            report.Write(_log.ToString());
        foreach (var (name, data) in _files)
        {
            using var entry = zip.CreateEntry(name).Open();
            entry.Write(data);
        }
        return path;
    }
}

/// <summary>Ошибка сайта поставщика с текстом для кассира.</summary>
public sealed class SiteException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Личный кабинет «Форум-Авто» (itrade.forum-auto.ru): вход по логину и паролю, архив заказов
/// (/shop/archive.html, по страницам — пока не дойдём до даты начала учёта) и возвраты (/shop/returns/index.html).
/// Страницы в windows-1251, данные — из таблиц.
/// </summary>
public sealed class ForumAutoSite : ISupplierSite
{
    public const string Id = "forum-auto";
    public const string Title = "Форум-Авто (itrade.forum-auto.ru)";

    private const string Base = "https://itrade.forum-auto.ru";
    private const int MaxArchivePages = 15;

    static ForumAutoSite() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static Encoding Cp1251 => Encoding.GetEncoding(1251);

    public async Task<SiteData> FetchAsync(string login, string password, DateTime since, CancellationToken cancel,
        SiteDebug? debug = null)
    {
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) OfficeChat-Cash");
        debug?.Line($"Сайт {Base}, логин «{login}», пароль: {password.Length} символов, учёт с {since:dd.MM.yyyy}");
        try
        {
            // Вход: та же форма, что на главной странице.
            var start = await GetAsync(http, "/shop/index.html", cancel, debug, "index");
            var csrf = ForumAutoParser.CsrfToken(start);
            debug?.Line($"csrf_token на странице входа: {(csrf.Length > 0 ? $"есть ({csrf.Length} символов)" : "нет")}; " +
                        $"форма входа на странице: {(ForumAutoParser.IsLoginPage(start) ? "есть" : "нет")}");
            var form = new StringBuilder();
            var shown = new StringBuilder();
            void Field(string name, string value)
            {
                if (form.Length > 0)
                {
                    form.Append('&');
                    shown.Append('&');
                }
                form.Append(name).Append('=').Append(HttpUtility.UrlEncode(value, Cp1251));
                shown.Append(name).Append('=').Append(name == "password" ? "***" : HttpUtility.UrlEncode(value, Cp1251));
            }
            Field("csrf_token", csrf);
            Field("login", login);
            Field("password", password);
            Field("enter", "Войти");
            Field("check", "stopSpam");
            debug?.Line($"POST /shop/index.html: {shown}");
            using (var content = new StringContent(form.ToString(), Encoding.ASCII, "application/x-www-form-urlencoded"))
            using (var response = await http.PostAsync(Base + "/shop/index.html", content, cancel))
            {
                var body = await ReadAsync(response, cancel, debug, "login-response");
                response.EnsureSuccessStatusCode();
                debug?.Line($"После входа: форма входа на странице — {(ForumAutoParser.IsLoginPage(body) ? "ЕСТЬ (вход, похоже, не удался)" : "нет")}");
            }
            debug?.Line("Cookies после входа: " +
                        string.Join(", ", cookies.GetAllCookies().Select(c => $"{c.Name} (домен {c.Domain}, {c.Value.Length} символов)")));

            var orders = new List<ExternalOrder>();
            for (var page = 1; page <= MaxArchivePages; page++)
            {
                var html = await GetAsync(http, page == 1 ? "/shop/archive.html" : $"/shop/archive.html?page={page}", cancel, debug,
                    $"archive-page{page}");
                if (ForumAutoParser.IsLoginPage(html))
                {
                    debug?.Line("Вместо архива — форма входа: сайт не пустил");
                    throw new SiteException("Сайт не пустил: проверьте логин и пароль");
                }
                var rows = ForumAutoParser.ParseOrders(html, debug);
                debug?.Line($"Архив, страница {page}: заказов разобрано {rows.Count}, из них с {since:dd.MM.yyyy} — {rows.Count(o => o.Date >= since)}; " +
                            $"всего страниц по сайту: {ForumAutoParser.PageCount(html)}");
                if (page == 1 && rows.Count == 0)
                {
                    // Страница пришла, а заказов в ней не нашли: сохраняем её — по ней можно поправить разбор.
                    var saved = SaveForDiagnostics("forum-archive.html", html);
                    throw new SiteException(ForumAutoParser.IsArchivePage(html)
                        ? $"В архиве заказов не нашли ни одного заказа — страница сохранена: {saved}"
                        : $"Вместо архива заказов сайт показал другую страницу — она сохранена: {saved}");
                }
                orders.AddRange(rows.Where(o => o.Date >= since));
                // Заказы — от новых к старым: дошли до даты начала учёта или страницы кончились.
                if (rows.Count == 0 || rows.Min(o => o.Date) < since || page >= ForumAutoParser.PageCount(html)) break;
            }

            var returnsHtml = await GetAsync(http, "/shop/returns/index.html", cancel, debug, "returns");
            var allReturns = ForumAutoParser.ParseReturns(returnsHtml, debug);
            if (allReturns.Count == 0 && returnsHtml.Contains("/returns/", StringComparison.OrdinalIgnoreCase))
                Log.Warn($"Касса: на странице возвратов не нашли ни одного возврата — страница сохранена: {SaveForDiagnostics("forum-returns.html", returnsHtml)}");
            var returns = allReturns.Where(r => r.Date >= since.Date).ToList();
            debug?.Line($"Возвраты: разобрано {allReturns.Count}, с {since:dd.MM.yyyy} — {returns.Count}, " +
                        $"из них «на баланс» — {returns.Count(r => r.Status.Contains("на баланс", StringComparison.OrdinalIgnoreCase))}");
            var result = new SiteData(orders.DistinctBy(o => o.Number).ToList(), returns);
            debug?.Line($"ИТОГ: заказов с {since:dd.MM.yyyy}: {result.Orders.Count}");
            foreach (var order in result.Orders) debug?.Line($"  заказ {order.Number} {order.Date:dd.MM.yyyy HH:mm} «{order.Status}» {order.SumText}");
            foreach (var item in result.Returns) debug?.Line($"  возврат {item.Number} {item.Date:dd.MM.yyyy} «{item.Status}» {Money.Format(item.Sum)}");
            return result;
        }
        catch (Exception ex) when (debug != null)
        {
            debug.Line($"ОШИБКА: {ex}");
            throw ex is HttpRequestException ? new SiteException("Сайт поставщика недоступен: " + ex.Message, ex)
                : ex is TaskCanceledException && !cancel.IsCancellationRequested ? new SiteException("Сайт поставщика не ответил вовремя", ex)
                : ex;
        }
        catch (HttpRequestException ex)
        {
            throw new SiteException("Сайт поставщика недоступен: " + ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested)
        {
            throw new SiteException("Сайт поставщика не ответил вовремя", ex);
        }
    }

    private static async Task<string> GetAsync(HttpClient http, string path, CancellationToken cancel, SiteDebug? debug, string name)
    {
        debug?.Line($"GET {path}");
        using var response = await http.GetAsync(Base + path, cancel);
        var html = await ReadAsync(response, cancel, debug, name);
        response.EnsureSuccessStatusCode();
        return html;
    }

    /// <summary>Тело ответа текстом (и в отладочный отчёт — как пришло, с заголовками).</summary>
    private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken cancel, SiteDebug? debug, string name)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
        // Кодировка: из заголовка ответа, иначе из <meta charset> в начале страницы, иначе windows-1251.
        var charset = response.Content.Headers.ContentType?.CharSet;
        var from = "заголовок";
        if (string.IsNullOrEmpty(charset))
        {
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            if (Regex.Match(head, @"charset\s*=\s*[""']?([\w-]+)", RegexOptions.IgnoreCase) is { Success: true } m)
            {
                charset = m.Groups[1].Value;
                from = "meta";
            }
            else from = "по умолчанию";
        }
        var encoding = charset != null && charset.Contains("utf", StringComparison.OrdinalIgnoreCase) ? Encoding.UTF8 : Cp1251;
        var html = encoding.GetString(bytes);
        if (debug != null)
        {
            var file = debug.AddFile(name + ".html", bytes);
            var setCookies = response.Headers.TryGetValues("Set-Cookie", out var values)
                ? string.Join(", ", values.Select(v => v.Split('=')[0])) : "нет";
            debug.Line($"  ответ {(int)response.StatusCode} {response.ReasonPhrase}, итоговый адрес {response.RequestMessage?.RequestUri}, " +
                       $"{bytes.Length} байт, Content-Type «{response.Content.Headers.ContentType}», кодировка {encoding.WebName} ({from}), " +
                       $"Set-Cookie: {setCookies}; сохранено как {file}");
            debug.Line($"  заголовок страницы: «{Regex.Match(html, "<title>(.*?)</title>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value.Trim()}», " +
                       $"«Архив заказов»: {(ForumAutoParser.IsArchivePage(html) ? "есть" : "нет")}, " +
                       $"<tr: {Regex.Count(html, "<tr", RegexOptions.IgnoreCase)}, paid_view: {Regex.Count(html, "paid_view", RegexOptions.IgnoreCase)}, " +
                       $"/returns/: {Regex.Count(html, "/returns/", RegexOptions.IgnoreCase)}");
        }
        return html;
    }

    /// <summary>Сохранить страницу сайта в папку журналов (для разбора, если что-то не прочиталось). Возвращает путь.</summary>
    private static string SaveForDiagnostics(string name, string html)
    {
        var path = Path.Combine(Log.Folder, name);
        try
        {
            File.WriteAllText(path, html, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось сохранить {path}", ex);
        }
        return path;
    }
}

/// <summary>
/// Разбор страниц «Форум-Авто» (отдельно — чтобы проверять тестами). Терпим к разметке: регистр тегов, кавычки
/// в атрибутах, незакрытые &lt;tr&gt;/&lt;td&gt;, переносы строк и &lt;br&gt; внутри ячеек.
/// </summary>
public static partial class ForumAutoParser
{
    // Строка таблицы: от <tr …> до </tr>, следующей <tr> или конца таблицы.
    [GeneratedRegex(@"<tr\b[^>]*>(.*?)(?=</tr\s*>|<tr\b|</table\s*>)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex RowRegex();

    // Ячейка: от <td …> до </td>, следующей <td> или конца строки.
    [GeneratedRegex(@"<td\b[^>]*>(.*?)(?=</td\s*>|<td\b|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();

    [GeneratedRegex(@"(\d{2}\.\d{2}\.\d{4})(?:\s+(\d{1,2}:\d{2}(?::\d{2})?))?")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"paid_view\.html\?paid=", RegexOptions.IgnoreCase)]
    private static partial Regex OrderLinkRegex();

    [GeneratedRegex(@"/returns/\d+\.html", RegexOptions.IgnoreCase)]
    private static partial Regex ReturnLinkRegex();

    [GeneratedRegex(@"name\s*=\s*[""']?csrf_token[""']?[^>]*?value\s*=\s*[""']([^""']*)[""']|name\s*=\s*[""']csrf-token[""']\s+content\s*=\s*[""']([^""']*)[""']",
        RegexOptions.IgnoreCase)]
    private static partial Regex CsrfRegex();

    [GeneratedRegex(@"name\s*=\s*[""']?password[""'\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordFieldRegex();

    [GeneratedRegex(@"Страниц\s*\((\d+)\)")]
    private static partial Regex PagesRegex();

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static string CsrfToken(string html)
    {
        foreach (Match match in CsrfRegex().Matches(html))
        {
            var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (value.Length > 0) return value;
        }
        return "";
    }

    /// <summary>Нас не впустили: на странице снова форма входа, а архива нет.</summary>
    public static bool IsLoginPage(string html) =>
        PasswordFieldRegex().IsMatch(html) && !html.Contains("Архив заказов", StringComparison.Ordinal);

    /// <summary>Это страница архива заказов (по заголовку).</summary>
    public static bool IsArchivePage(string html) => html.Contains("Архив заказов", StringComparison.Ordinal);

    public static int PageCount(string html) =>
        PagesRegex().Match(html) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var pages) ? pages : 1;

    /// <summary>Заказы из архива: строки со ссылкой на заказ (paid_view.html?paid=…): номер, дата, статус, сумма.</summary>
    public static List<ExternalOrder> ParseOrders(string html, SiteDebug? debug = null)
    {
        var result = new List<ExternalOrder>();
        var index = 0;
        foreach (Match row in RowRegex().Matches(html))
        {
            index++;
            if (!OrderLinkRegex().IsMatch(row.Value)) continue;
            var cells = Cells(row.Groups[1].Value);
            void Skip(string why) => debug?.Line($"    строка {index}: пропущена — {why}; ячейки: [{string.Join(" | ", cells)}]");
            var dateIndex = cells.FindIndex(c => DateRegex().IsMatch(c));
            if (dateIndex < 1 || dateIndex + 2 >= cells.Count)
            {
                Skip(dateIndex < 0 ? "нет ячейки с датой" : "после даты нет статуса и суммы");
                continue;
            }
            var number = new string(cells[dateIndex - 1].Where(char.IsDigit).ToArray());
            if (number.Length == 0)
            {
                Skip("нет номера заказа перед датой");
                continue;
            }
            if (ParseDate(cells[dateIndex]) is not { } date)
            {
                Skip("дата не читается");
                continue;
            }
            if (Money.Parse(cells[dateIndex + 2]) is not { } sum)
            {
                Skip($"сумма «{cells[dateIndex + 2]}» не читается");
                continue;
            }
            result.Add(new ExternalOrder(number, date, cells[dateIndex + 1], sum));
        }
        debug?.Line($"    разбор заказов: строк таблиц {index}, со ссылкой на заказ — {Regex.Count(html, @"paid_view\.html\?paid=", RegexOptions.IgnoreCase)}, " +
                    $"разобрано {result.Count}");
        return result;
    }

    /// <summary>Возвраты: ключ, №, дата, менеджер, статус, причина, штраф, на сумму.</summary>
    public static List<ExternalReturn> ParseReturns(string html, SiteDebug? debug = null)
    {
        var result = new List<ExternalReturn>();
        var index = 0;
        var skipped = 0;
        foreach (Match row in RowRegex().Matches(html))
        {
            index++;
            if (!ReturnLinkRegex().IsMatch(row.Value)) continue;
            var cells = Cells(row.Groups[1].Value);
            var number = cells.Count > 0 ? new string(cells[0].Where(char.IsDigit).ToArray()) : "";
            var why = cells.Count < 8 ? $"ячеек {cells.Count}, нужно 8"
                : number.Length == 0 ? "нет номера"
                : ParseDate(cells[2]) == null ? "дата не читается"
                : Money.Parse(cells[7]) == null ? $"сумма «{cells[7]}» не читается"
                : null;
            if (why != null)
            {
                // Пропуски пишем не все — первых двадцати хватит, чтобы понять причину.
                if (++skipped <= 20) debug?.Line($"    возврат, строка {index}: пропущена — {why}; ячейки: [{string.Join(" | ", cells)}]");
                continue;
            }
            result.Add(new ExternalReturn(number, ParseDate(cells[2])!.Value, cells[4], cells[5], cells[6], Money.Parse(cells[7])!.Value));
        }
        debug?.Line($"    разбор возвратов: строк таблиц {index}, разобрано {result.Count}, пропущено {skipped}");
        return result;
    }

    private static DateTime? ParseDate(string text)
    {
        if (DateRegex().Match(text) is not { Success: true } m) return null;
        var value = m.Groups[2].Success ? $"{m.Groups[1].Value} {m.Groups[2].Value}" : m.Groups[1].Value;
        string[] formats = ["dd.MM.yyyy H:mm:ss", "dd.MM.yyyy H:mm", "dd.MM.yyyy"];
        return DateTime.TryParseExact(value, formats, Russian, DateTimeStyles.None, out var date) ? date : null;
    }

    private static List<string> Cells(string row) => CellRegex().Matches(row)
        .Select(m => SpacesRegex().Replace(WebUtility.HtmlDecode(TagRegex().Replace(m.Groups[1].Value, " ")).Replace('\u00A0', ' '), " ").Trim())
        .ToList();
}
