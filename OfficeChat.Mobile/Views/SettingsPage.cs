using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OfficeChat.Client;
using OfficeChat.Services;

namespace OfficeChat.Mobile.Views;

/// <summary>Аккаунт на телефоне: имя, пароль, устройства, выход.</summary>
public sealed class SettingsPage : Page
{
    private readonly StackPanel _list = new() { Margin = new Avalonia.Thickness(16), Spacing = 10 };
    private readonly TextBlock _nameText = new() { FontSize = 18, FontWeight = FontWeight.SemiBold };

    public SettingsPage()
    {
        var back = new Button { Content = "←", Classes = { "header" } };
        back.Click += (_, _) => Shell.Pop();
        var header = new Border
        {
            Background = (IBrush)Avalonia.Application.Current!.FindResource("HeaderBrush")!,
            Padding = new Avalonia.Thickness(2, 8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { back, new TextBlock { Text = "Аккаунт", FontSize = 18, FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center } },
            },
        };
        var account = MobileSession.Account;
        _nameText.Text = MobileSession.Settings?.DisplayName ?? "";
        _list.Children.Add(_nameText);
        _list.Children.Add(new TextBlock
        {
            Text = account == null ? "" : $"Логин: {account.Login}\nСервер: {account.ServerName} ({account.ServerHost})",
            Foreground = (IBrush)Avalonia.Application.Current!.FindResource("MutedTextBrush")!, TextWrapping = TextWrapping.Wrap,
        });
        _list.Children.Add(Action("Изменить имя", ChangeNameAsync));
        _list.Children.Add(Action("Сменить пароль", ChangePasswordAsync));
        _list.Children.Add(Action("Выйти из аккаунта", SignOutAsync));
        _list.Children.Add(new TextBlock
        {
            Margin = new Avalonia.Thickness(0, 16, 0, 0), TextWrapping = TextWrapping.Wrap, FontSize = 13,
            Foreground = (IBrush)Avalonia.Application.Current!.FindResource("MutedTextBrush")!,
            Text = "Чтобы сообщения приходили сразу, OfficeChat работает в фоне — значок в строке уведомлений. " +
                   "Если телефон всё равно его закрывает, разрешите работу без ограничений в настройках батареи.",
        });
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(new ScrollViewer { Content = _list });
        Content = dock;
    }

    private static Button Action(string text, Func<Task> action)
    {
        var button = new Button
        {
            Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Avalonia.Thickness(14, 12), FontSize = 16,
        };
        button.Click += async (_, _) => await action();
        return button;
    }

    private async Task ChangeNameAsync()
    {
        if (MobileSession.Chat?.Session is not { } session || MobileSession.Account is not { } account ||
            MobileSession.Settings is not { } settings) return;
        var name = (await Dialogs.InputAsync(this, "Имя (его видят коллеги)", settings.DisplayName))?.Trim();
        if (string.IsNullOrEmpty(name) || name == settings.DisplayName) return;
        try
        {
            await session.Api.ChangeProfileAsync(name);
            account.DisplayName = name;
            AccountStore.Save(account, makeLastUsed: false);
            settings.DisplayName = name;
            SettingsService.Save(settings);
            _nameText.Text = name;
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, "Не удалось сменить имя: " + LoginFlow.Describe(ex));
        }
    }

    private async Task ChangePasswordAsync()
    {
        if (MobileSession.Chat?.Session is not { } session) return;
        var old = new TextBox { PasswordChar = '•', Watermark = "текущий пароль" };
        var first = new TextBox { PasswordChar = '•', Watermark = "новый пароль" };
        var second = new TextBox { PasswordChar = '•', Watermark = "новый пароль ещё раз" };
        var form = new StackPanel { Spacing = 8, Children = { old, first, second } };
        if (!await Dialogs.FormAsync(this, "Смена пароля", form, "Сменить")) return;
        if (first.Text != second.Text)
        {
            await Dialogs.InfoAsync(this, "Новые пароли не совпадают");
            return;
        }
        try
        {
            await session.Api.ChangePasswordAsync(old.Text ?? "", first.Text ?? "");
            await Dialogs.InfoAsync(this, "Пароль изменён. На других устройствах вводить его не нужно.");
        }
        catch (Exception ex)
        {
            await Dialogs.InfoAsync(this, "Пароль не изменён: " + LoginFlow.Describe(ex));
        }
    }

    private async Task SignOutAsync()
    {
        if (!await Dialogs.ConfirmAsync(this, "Выйти из аккаунта на этом телефоне? Переписка останется на сервере.", "Выйти"))
            return;
        await MobileSession.SignOutAsync();
        Shell.SetRoot(new LoginPage());
    }
}
