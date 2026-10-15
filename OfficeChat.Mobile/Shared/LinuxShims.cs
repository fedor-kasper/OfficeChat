using Avalonia.Controls;

namespace OfficeChat.Views;

/// <summary>
/// Для общих с Linux-версией экранов (панель игры): вопрос «Да/Нет» — на телефоне поверх экрана, а не окном.
/// </summary>
internal static class Dialogs
{
    public static Task<bool> Confirm(Window? owner, string text, string title = "OfficeChat") =>
        Mobile.ShellView.Instance is { } shell ? Mobile.Dialogs.ConfirmAsync(shell, text) : Task.FromResult(false);
}
