using OfficeChat.Services;

namespace OfficeChat.Server.Tests;

/// <summary>Списки при наборе (как в Markdown) — общая логика приложений.</summary>
public class ListContinuationTests
{
    // Набрали перевод строки в позиции «|» (в тексте он уже есть перед «|»).
    private static string Type(string withCaret)
    {
        var caret = withCaret.IndexOf('|');
        var text = withCaret.Remove(caret, 1);
        var result = ListContinuation.AfterNewLine(text, caret);
        return result is { } r ? r.Text.Insert(r.Caret, "|") : withCaret;
    }

    [Theory]
    [InlineData("1. купить хлеб\n|", "1. купить хлеб\n2. |")]
    [InlineData("Список:\n9) раз\n|", "Список:\n9) раз\n10) |")]
    [InlineData("- молоко\n|", "- молоко\n- |")]
    [InlineData("  • отступ\n|", "  • отступ\n  • |")]
    [InlineData("1. первый\n2. \n|", "1. первый\n|")]
    [InlineData("- пункт\n- \n|", "- пункт\n|")]
    [InlineData("обычный текст\n|", "обычный текст\n|")]
    [InlineData("1.5 литра\n|", "1.5 литра\n|")]
    [InlineData("\n|", "\n|")]
    public void Continues_and_ends_lists(string before, string after) => Assert.Equal(after, Type(before));

    [Fact]
    public void Inserted_item_renumbers_the_rest()
    {
        Assert.Equal("1. а\n2. |\n3. б\n4. в\nтекст\n3. не трогаем",
            Type("1. а\n|\n2. б\n3. в\nтекст\n3. не трогаем"));
    }
}
