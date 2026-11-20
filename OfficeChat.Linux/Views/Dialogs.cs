using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OfficeChat.Models;

namespace OfficeChat.Views;

/// <summary>Простые окна-сообщения (в Avalonia нет MessageBox).</summary>
public static class Dialogs
{
    /// <summary>Сообщение с кнопкой «ОК».</summary>
    public static Task Info(Window? owner, string text, string title = "OfficeChat") =>
        Show(owner, text, title, yesNo: false);

    /// <summary>Вопрос «Да / Нет». true — «Да».</summary>
    public static Task<bool> Confirm(Window? owner, string text, string title = "OfficeChat") =>
        Show(owner, text, title, yesNo: true);

    /// <summary>Смена пароля: (текущий, новый) или (null, null), если отменили.</summary>
    public static async Task<(string? Old, string? New)> ChangePassword(Window owner)
    {
        (string?, string?) result = (null, null);
        var window = new Window
        {
            Title = "Смена пароля",
            Width = 380,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = Platform.AppIcon.Window,
        };
        TextBox Field() => new() { PasswordChar = '•' };
        TextBlock Label(string text) => new() { Text = text, FontSize = 12, Margin = new Thickness(0, 10, 0, 4) };
        var current = Field();
        var fresh = Field();
        var repeat = Field();
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var ok = new Button { Content = "Сменить", MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        var cancel = new Button { Content = "Отмена", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        ok.Click += (_, _) =>
        {
            var newPassword = fresh.Text ?? "";
            error.IsVisible = true;
            if (newPassword.Length < Protocol.ServerProtocol.MinPasswordLength)
                error.Text = $"Новый пароль — не короче {Protocol.ServerProtocol.MinPasswordLength} символов";
            else if (newPassword != repeat.Text)
                error.Text = "Пароли не совпадают";
            else
            {
                result = (current.Text ?? "", newPassword);
                window.Close();
            }
        };
        cancel.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                Label("Текущий пароль"), current, Label("Новый пароль"), fresh, Label("Новый пароль ещё раз"), repeat,
                error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Margin = new Thickness(0, 16, 0, 0), Children = { ok, cancel },
                },
            },
        };
        window.Opened += (_, _) => current.Focus();
        await window.ShowDialog(owner);
        return result;
    }

    /// <summary>Ввод строки. null — отменили (или оставили пустым, когда <paramref name="required"/>).</summary>
    public static async Task<string?> Input(Window owner, string title, string prompt, string initial, string okText,
        bool required = false)
    {
        string? result = null;
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = Platform.AppIcon.Window,
        };
        var box = new TextBox { Text = initial, MaxLength = 500 };
        var ok = new Button { Content = okText, MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        var cancel = new Button { Content = "Отмена", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        void Update() => ok.IsEnabled = !required || !string.IsNullOrWhiteSpace(box.Text);
        box.TextChanged += (_, _) => Update();
        Update();
        ok.Click += (_, _) =>
        {
            result = (box.Text ?? "").Trim();
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, FontSize = 14 },
                box,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Margin = new Thickness(0, 8, 0, 0), Children = { ok, cancel },
                },
            },
        };
        window.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        await window.ShowDialog(owner);
        return required && string.IsNullOrWhiteSpace(result) ? null : result;
    }

    /// <summary>Режим «Касса»: включить и код кассы. null — отменили.</summary>
    public static async Task<(bool Enabled, string Code)?> CashSettings(Window owner, bool enabled, string code, string status)
    {
        (bool, string)? result = null;
        var window = new Window
        {
            Title = "Режим «Касса»",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = Platform.AppIcon.Window,
        };
        TextBlock Hint(string text) => new()
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
        };
        var enable = new CheckBox { Content = "Включить режим «Касса»", IsChecked = enabled, FontSize = 14 };
        var codeBox = new TextBox { Text = code, MaxLength = 100, Watermark = "например: магазин-ленина-5-касса" };
        var ok = new Button { Content = "Сохранить", MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        var cancel = new Button { Content = "Отмена", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        ok.Click += (_, _) =>
        {
            result = (enable.IsChecked == true, (codeBox.Text ?? "").Trim());
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 8,
            Children =
            {
                enable,
                Hint("Слева появится список поставщиков с балансом: поставки (сколько по накладной и сколько выдали), " +
                     "возвраты и погашения. Излишки от округления копятся на балансе поставщика."),
                new TextBlock { Text = "Код кассы", FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) },
                codeBox,
                Hint("Компьютеры с одинаковым кодом ведут общий журнал — по локальной сети, без сервера. Код — как пароль: " +
                     "без него чужие компьютеры записи не прочитают и не подделают. Без кода касса только на этом компьютере."),
            },
        };
        if (status.Length > 0) panel.Children.Add(Hint(status));
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0), Children = { ok, cancel },
        });
        window.Content = panel;
        await window.ShowDialog(owner);
        return result;
    }

    /// <summary>Что ввели в окне «Сайт поставщика». Пустой логин — отключить сайт.</summary>
    public sealed record SiteLinkResult(string Login, string Password, DateTime Since, long? StartBalance);

    /// <summary>Подключить поставщика к сайту (Форум-Авто): логин, пароль, с какой даты учитывать, начальный баланс.</summary>
    public static async Task<SiteLinkResult?> SiteLink(Window owner, string login, bool linked, DateTime since)
    {
        SiteLinkResult? result = null;
        var window = new Window
        {
            Title = "Сайт поставщика",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = Platform.AppIcon.Window,
        };
        TextBlock Label(string text) => new() { Text = text, FontSize = 12, Margin = new Thickness(0, 10, 0, 3) };
        TextBlock Hint(string text) => new()
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#6B7280")),
        };
        var loginBox = new TextBox { Text = login, MaxLength = 100 };
        var passwordBox = new TextBox { PasswordChar = '•', MaxLength = 200, Watermark = linked ? "не меняется, если оставить пустым" : "" };
        var sinceBox = new TextBox { Text = since.ToString("dd.MM.yyyy"), Width = 140, HorizontalAlignment = HorizontalAlignment.Left };
        var balanceBox = new TextBox { Width = 140, HorizontalAlignment = HorizontalAlignment.Left, Watermark = "не менять" };
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var ok = new Button { Content = "Сохранить", MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        var off = new Button { Content = "Отключить", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsVisible = linked };
        var cancel = new Button { Content = "Отмена", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        ok.Click += (_, _) =>
        {
            var name = (loginBox.Text ?? "").Trim();
            long? balance = null;
            error.IsVisible = true;
            if (name.Length == 0) error.Text = "Введите логин";
            else if (!linked && string.IsNullOrEmpty(passwordBox.Text)) error.Text = "Введите пароль";
            else if (!DateTime.TryParseExact((sinceBox.Text ?? "").Trim(), "dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture,
                         System.Globalization.DateTimeStyles.None, out var date))
                error.Text = "Дата — в виде 20.11.2026";
            else if (!string.IsNullOrWhiteSpace(balanceBox.Text) && (balance = Money.ParseSigned(balanceBox.Text)) == null)
                error.Text = "Начальный баланс — число, например 1500 или -250,50";
            else
            {
                result = new SiteLinkResult(name, passwordBox.Text ?? "", date, balance);
                window.Close();
            }
        };
        off.Click += (_, _) =>
        {
            result = new SiteLinkResult("", "", DateTime.Today, null);
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = "Форум-Авто (itrade.forum-auto.ru)", FontSize = 15, FontWeight = FontWeight.SemiBold },
                Hint("Касса сама будет заходить в личный кабинет: заказы из архива появятся в списке «ждут оплаты» " +
                     "(остаётся ввести, сколько отдали), а возвраты «Поставлен на баланс» сразу прибавятся к балансу."),
                Label("Логин"), loginBox,
                Label("Пароль"), passwordBox,
                Hint("Пароль хранится в журнале кассы зашифрованным кодом кассы — им пользуются все компьютеры этой кассы."),
                Label("Учитывать заказы и возвраты с даты"), sinceBox,
                Label("Баланс поставщика на эту дату (необязательно)"), balanceBox,
                Hint("Чтобы начать учёт с известной суммы: баланс станет таким, дальше пойдут заказы и возвраты с сайта."),
                error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Margin = new Thickness(0, 16, 0, 0), Children = { off, ok, cancel },
                },
            },
        };
        window.Opened += (_, _) => (login.Length == 0 ? loginBox : passwordBox).Focus();
        await window.ShowDialog(owner);
        return result;
    }

    /// <summary>Корректировка баланса: новая сумма и причина. null — отменили.</summary>
    public static async Task<(long Balance, string Reason)?> Correction(Window owner, string supplier, long current)
    {
        (long, string)? result = null;
        var window = new Window
        {
            Title = "Корректировка баланса",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Icon = Platform.AppIcon.Window,
        };
        var amount = new TextBox { Text = Money.FormatInput(current), Width = 160, HorizontalAlignment = HorizontalAlignment.Left };
        var reason = new TextBox { MaxLength = 500, Watermark = "например: сверка с поставщиком" };
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, IsVisible = false };
        var ok = new Button { Content = "Записать", MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        var cancel = new Button { Content = "Отмена", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        ok.Click += (_, _) =>
        {
            error.IsVisible = true;
            if (Money.ParseSigned(amount.Text) is not { } balance) error.Text = "Баланс — число, например 1500 или -250,50";
            else if (string.IsNullOrWhiteSpace(reason.Text)) error.Text = "Укажите причину — она останется в истории";
            else
            {
                result = (balance, reason.Text.Trim());
                window.Close();
            }
        };
        cancel.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = $"{supplier}: сейчас {Money.Format(current)}. С этого момента баланс станет таким, как укажете " +
                           "(запись с причиной останется в истории).",
                    TextWrapping = TextWrapping.Wrap, FontSize = 13,
                },
                new TextBlock { Text = "Новый баланс, ₽", FontSize = 12, Margin = new Thickness(0, 8, 0, 0) }, amount,
                new TextBlock { Text = "Причина", FontSize = 12, Margin = new Thickness(0, 8, 0, 0) }, reason,
                error,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8,
                    Margin = new Thickness(0, 12, 0, 0), Children = { ok, cancel },
                },
            },
        };
        window.Opened += (_, _) =>
        {
            amount.Focus();
            amount.SelectAll();
        };
        await window.ShowDialog(owner);
        return result;
    }

    private static async Task<bool> Show(Window? owner, string text, string title, bool yesNo)
    {
        var result = false;
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Icon = Platform.AppIcon.Window,
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var ok = new Button { Content = yesNo ? "Да" : "ОК", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true };
        ok.Click += (_, _) => { result = true; window.Close(); };
        buttons.Children.Add(ok);
        if (yesNo)
        {
            var no = new Button { Content = "Нет", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
            no.Click += (_, _) => window.Close();
            buttons.Children.Add(no);
        }

        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 },
                buttons,
            },
        };

        if (owner is { IsVisible: true })
            await window.ShowDialog(owner);
        else
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }
        return result;
    }
}
