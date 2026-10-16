using System.Text.RegularExpressions;

namespace OfficeChat.Services;

/// <summary>
/// Списки при наборе, как в Markdown: после «1. пункт» новая строка начинается с «2. », после «- пункт» — с «- ».
/// Пустой пункт + новая строка — конец списка (маркер убирается). Если пункт вставили в середину нумерованного
/// списка, следующие номера сдвигаются.
/// </summary>
public static partial class ListContinuation
{
    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<number>\d{1,4})(?<sep>[.)])[ \t]+(?<rest>.*)$")]
    private static partial Regex NumberedRegex();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<bullet>[-*•])[ \t]+(?<rest>.*)$")]
    private static partial Regex BulletRegex();

    /// <summary>
    /// В текст только что вставили перевод строки перед позицией caret (text[caret - 1] == '\n').
    /// Возвращает новый текст и позицию курсора — или null, если это не список и менять ничего не нужно.
    /// </summary>
    public static (string Text, int Caret)? AfterNewLine(string text, int caret)
    {
        if (caret <= 0 || caret > text.Length || text[caret - 1] != '\n') return null;
        var lineEnd = caret - 1;
        if (lineEnd == 0) return null;
        var lineStart = text.LastIndexOf('\n', lineEnd - 1) + 1;
        var line = text[lineStart..lineEnd].TrimEnd('\r');

        string marker;
        string indent;
        string rest;
        int? number = null;
        var sep = "";
        if (NumberedRegex().Match(line) is { Success: true } numbered)
        {
            indent = numbered.Groups["indent"].Value;
            number = int.Parse(numbered.Groups["number"].Value);
            sep = numbered.Groups["sep"].Value;
            rest = numbered.Groups["rest"].Value;
            marker = $"{indent}{number + 1}{sep} ";
        }
        else if (BulletRegex().Match(line) is { Success: true } bullet)
        {
            indent = bullet.Groups["indent"].Value;
            rest = bullet.Groups["rest"].Value;
            marker = $"{indent}{bullet.Groups["bullet"].Value} ";
        }
        else
        {
            return null;
        }

        // Пустой пункт: список закончен — убираем маркер и только что набранный перевод строки.
        if (rest.Trim().Length == 0)
            return (text[..lineStart] + text[caret..], lineStart);

        var after = text[caret..];
        if (number != null) after = Renumber(after, indent, sep, number.Value + 2);
        return (text[..caret] + marker + after, caret + marker.Length);
    }

    /// <summary>
    /// Нумерованные строки сразу после вставленного пункта (тот же отступ) — с номерами по порядку.
    /// text — от курсора: сначала остаток строки нового пункта, со следующей строки — прежние пункты.
    /// </summary>
    private static string Renumber(string text, string indent, string sep, int next)
    {
        var lines = text.Split('\n');
        for (var i = 1; i < lines.Length; i++)
        {
            var match = NumberedRegex().Match(lines[i].TrimEnd('\r'));
            if (!match.Success || match.Groups["indent"].Value != indent || match.Groups["sep"].Value != sep) break;
            var numberGroup = match.Groups["number"];
            lines[i] = lines[i][..numberGroup.Index] + next++ + lines[i][(numberGroup.Index + numberGroup.Length)..];
        }
        return string.Join('\n', lines);
    }
}
