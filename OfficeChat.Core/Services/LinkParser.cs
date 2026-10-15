using System.Text.RegularExpressions;

namespace OfficeChat.Services;

/// <summary>Кусок текста сообщения: обычный текст (<see cref="Link"/> == null) или ссылка.</summary>
public sealed record TextSegment(string Text, string? Link)
{
    public bool IsLink => Link != null;
}

/// <summary>
/// Находит в тексте сообщения ссылки, по которым можно кликнуть:
/// http(s)://…, www.… и сетевые пути \\сервер\папка (их часто пересылают в офисе).
/// </summary>
public static partial class LinkParser
{
    [GeneratedRegex(@"(?:https?://|ftp://|www\.)[^\s<>""«»]+|\\\\[^\s\\/:*?""<>|]+\\[^\s<>""|]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();

    /// <summary>Знаки, которыми обычно заканчивается предложение, а не ссылка.</summary>
    private const string TrailingPunctuation = ".,;:!?…'\"»)]}";

    public static bool ContainsLink(string? text) => !string.IsNullOrEmpty(text) && Split(text).Any(s => s.IsLink);

    /// <summary>Разбивает текст на обычные куски и ссылки (вместе они дают исходный текст без изменений).</summary>
    public static IReadOnlyList<TextSegment> Split(string text)
    {
        var result = new List<TextSegment>();
        var position = 0;
        foreach (Match match in LinkPattern().Matches(text))
        {
            var candidate = TrimTrailing(match.Value);
            var link = ToLink(candidate);
            if (link == null) continue;

            if (match.Index > position)
                result.Add(new TextSegment(text[position..match.Index], null));
            result.Add(new TextSegment(candidate, link));
            position = match.Index + candidate.Length;
        }
        if (position < text.Length)
            result.Add(new TextSegment(text[position..], null));
        return result;
    }

    /// <summary>
    /// Убирает с конца знаки препинания: «Смотри https://site.ru/page.» — точка не часть ссылки.
    /// Закрывающую скобку оставляем, если в ссылке есть парная открывающая (адреса Википедии и т. п.).
    /// </summary>
    private static string TrimTrailing(string value)
    {
        while (value.Length > 0 && TrailingPunctuation.Contains(value[^1]))
        {
            var last = value[^1];
            if (last == ')' && value.Count(c => c == '(') >= value.Count(c => c == ')')) break;
            if (last == ']' && value.Count(c => c == '[') >= value.Count(c => c == ']')) break;
            value = value[..^1];
        }
        return value;
    }

    /// <summary>Что открывать по клику; null — это не ссылка (например, «www.» без адреса).</summary>
    private static string? ToLink(string candidate)
    {
        if (candidate.StartsWith(@"\\", StringComparison.Ordinal))
            return candidate.Length > 3 ? candidate : null;

        // «www.…» без схемы — открываем как http; внутренние адреса вида http://crm/ тоже ссылки.
        var url = candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + candidate : candidate;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https" or "ftp") || uri.Host.Length == 0) return null;
        return url;
    }

    /// <summary>Сетевой путь \\сервер\папка (а не веб-адрес).</summary>
    public static bool IsNetworkPath(string link) => link.StartsWith(@"\\", StringComparison.Ordinal);
}
