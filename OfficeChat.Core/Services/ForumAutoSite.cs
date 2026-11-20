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
                orders.AddRange(rows.Where(o => o.Date >= since));
                // Заказы — от новых к старым: дошли до даты начала учёта или страницы кончились.
                if (rows.Count == 0 || rows.Min(o => o.Date) < since || page >= ForumAutoParser.PageCount(html)) break;
            }

            var returnsHtml = await GetAsync(http, "/shop/returns/index.html", cancel);
            var returns = ForumAutoParser.ParseReturns(returnsHtml).Where(r => r.Date >= since.Date).ToList();
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
        var charset = response.Content.Headers.ContentType?.CharSet;
        return (charset != null && charset.Contains("utf", StringComparison.OrdinalIgnoreCase) ? Encoding.UTF8 : Cp1251).GetString(bytes);
    }
}

/// <summary>Разбор страниц «Форум-Авто» (отдельно — чтобы проверять тестами на сохранённых страницах).</summary>
public static partial class ForumAutoParser
{
    [GeneratedRegex(@"<tr class=""w"">(.*?)</tr>", RegexOptions.Singleline)]
    private static partial Regex RowRegex();

    [GeneratedRegex(@"<td[^>]*>(.*?)</td>", RegexOptions.Singleline)]
    private static partial Regex CellRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"name=""csrf_token""\s+value=""([^""]*)""|name=""csrf-token""\s+content=""([^""]*)""")]
    private static partial Regex CsrfRegex();

    [GeneratedRegex(@"Страниц\((\d+)\)")]
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

    /// <summary>Нас не впустили: на странице снова форма входа, а таблиц нет.</summary>
    public static bool IsLoginPage(string html) =>
        html.Contains("name=\"password\"", StringComparison.Ordinal) && !html.Contains("<h1>Архив заказов</h1>", StringComparison.Ordinal);

    public static int PageCount(string html) =>
        PagesRegex().Match(html) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var pages) ? pages : 1;

    /// <summary>Заказы из архива: строки с номером-ссылкой на заказ (paid_view.html?paid=…), датой, статусом и суммой.</summary>
    public static List<ExternalOrder> ParseOrders(string html)
    {
        var result = new List<ExternalOrder>();
        foreach (Match row in RowRegex().Matches(html))
        {
            if (!row.Value.Contains("paid_view.html?paid=", StringComparison.Ordinal)) continue;
            var cells = Cells(row.Groups[1].Value);
            if (cells.Count < 4 || cells[0].Length == 0) continue;
            if (!DateTime.TryParseExact(cells[1], "dd.MM.yyyy HH:mm:ss", Russian, DateTimeStyles.None, out var date)) continue;
            if (Money.Parse(cells[3]) is not { } sum) continue;
            result.Add(new ExternalOrder(cells[0], date, cells[2], sum));
        }
        return result;
    }

    /// <summary>Возвраты: ключ, №, дата, менеджер, статус, причина, штраф, на сумму.</summary>
    public static List<ExternalReturn> ParseReturns(string html)
    {
        var result = new List<ExternalReturn>();
        foreach (Match row in RowRegex().Matches(html))
        {
            if (!row.Value.Contains("/shop/returns/", StringComparison.Ordinal)) continue;
            var cells = Cells(row.Groups[1].Value);
            if (cells.Count < 8 || cells[0].Length == 0) continue;
            if (!DateTime.TryParseExact(cells[2], "dd.MM.yyyy", Russian, DateTimeStyles.None, out var date)) continue;
            if (Money.Parse(cells[7]) is not { } sum) continue;
            result.Add(new ExternalReturn(cells[0], date, cells[4], cells[5], cells[6], sum));
        }
        return result;
    }

    private static List<string> Cells(string row) => CellRegex().Matches(row)
        .Select(m => WebUtility.HtmlDecode(TagRegex().Replace(m.Groups[1].Value, "")).Replace(' ', ' ').Trim())
        .ToList();
}
