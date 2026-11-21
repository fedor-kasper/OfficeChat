using System.IO;
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
    /// <summary>Войти и прочитать заказы и возвраты не раньше <paramref name="since"/>. Ошибка — исключение с понятным текстом.</summary>
    Task<SiteData> FetchAsync(string login, string password, DateTime since, CancellationToken cancel);
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

    public async Task<SiteData> FetchAsync(string login, string password, DateTime since, CancellationToken cancel)
    {
        using var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) OfficeChat-Cash");
        try
        {
            // Вход: та же форма, что на главной странице.
            var start = await GetAsync(http, "/shop/index.html", cancel);
            var form = new StringBuilder();
            void Field(string name, string value)
            {
                if (form.Length > 0) form.Append('&');
                form.Append(name).Append('=').Append(HttpUtility.UrlEncode(value, Cp1251));
            }
            Field("csrf_token", ForumAutoParser.CsrfToken(start));
            Field("login", login);
            Field("password", password);
            Field("enter", "Войти");
            Field("check", "stopSpam");
            using (var content = new StringContent(form.ToString(), Encoding.ASCII, "application/x-www-form-urlencoded"))
            using (var response = await http.PostAsync(Base + "/shop/index.html", content, cancel))
                response.EnsureSuccessStatusCode();

            var orders = new List<ExternalOrder>();
            for (var page = 1; page <= MaxArchivePages; page++)
            {
                var html = await GetAsync(http, page == 1 ? "/shop/archive.html" : $"/shop/archive.html?page={page}", cancel);
                if (ForumAutoParser.IsLoginPage(html))
                    throw new SiteException("Сайт не пустил: проверьте логин и пароль");
                var rows = ForumAutoParser.ParseOrders(html);
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

            var returnsHtml = await GetAsync(http, "/shop/returns/index.html", cancel);
            var allReturns = ForumAutoParser.ParseReturns(returnsHtml);
            if (allReturns.Count == 0 && returnsHtml.Contains("/returns/", StringComparison.OrdinalIgnoreCase))
                Log.Warn($"Касса: на странице возвратов не нашли ни одного возврата — страница сохранена: {SaveForDiagnostics("forum-returns.html", returnsHtml)}");
            var returns = allReturns.Where(r => r.Date >= since.Date).ToList();
            return new SiteData(orders.DistinctBy(o => o.Number).ToList(), returns);
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

    private static async Task<string> GetAsync(HttpClient http, string path, CancellationToken cancel)
    {
        using var response = await http.GetAsync(Base + path, cancel);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
        // Кодировка: из заголовка ответа, иначе из <meta charset> в начале страницы, иначе windows-1251.
        var charset = response.Content.Headers.ContentType?.CharSet;
        if (string.IsNullOrEmpty(charset))
        {
            var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
            if (Regex.Match(head, @"charset\s*=\s*[""']?([\w-]+)", RegexOptions.IgnoreCase) is { Success: true } m) charset = m.Groups[1].Value;
        }
        return (charset != null && charset.Contains("utf", StringComparison.OrdinalIgnoreCase) ? Encoding.UTF8 : Cp1251).GetString(bytes);
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
    public static List<ExternalOrder> ParseOrders(string html)
    {
        var result = new List<ExternalOrder>();
        foreach (Match row in RowRegex().Matches(html))
        {
            if (!OrderLinkRegex().IsMatch(row.Value)) continue;
            var cells = Cells(row.Groups[1].Value);
            var dateIndex = cells.FindIndex(c => DateRegex().IsMatch(c));
            if (dateIndex < 1 || dateIndex + 2 >= cells.Count) continue;
            var number = new string(cells[dateIndex - 1].Where(char.IsDigit).ToArray());
            if (number.Length == 0 || ParseDate(cells[dateIndex]) is not { } date) continue;
            if (Money.Parse(cells[dateIndex + 2]) is not { } sum) continue;
            result.Add(new ExternalOrder(number, date, cells[dateIndex + 1], sum));
        }
        return result;
    }

    /// <summary>Возвраты: ключ, №, дата, менеджер, статус, причина, штраф, на сумму.</summary>
    public static List<ExternalReturn> ParseReturns(string html)
    {
        var result = new List<ExternalReturn>();
        foreach (Match row in RowRegex().Matches(html))
        {
            if (!ReturnLinkRegex().IsMatch(row.Value)) continue;
            var cells = Cells(row.Groups[1].Value);
            if (cells.Count < 8) continue;
            var number = new string(cells[0].Where(char.IsDigit).ToArray());
            if (number.Length == 0 || ParseDate(cells[2]) is not { } date) continue;
            if (Money.Parse(cells[7]) is not { } sum) continue;
            result.Add(new ExternalReturn(number, date, cells[4], cells[5], cells[6], sum));
        }
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
