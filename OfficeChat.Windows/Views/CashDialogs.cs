using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OfficeChat.Views;

/// <summary>Окна режима «Касса»: ввод строки и настройки кассы.</summary>
public static class CashDialogs
{
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));

    /// <summary>Ввод строки. null — отменили (или оставили пустым, когда <paramref name="required"/>).</summary>
    public static string? Input(Window? owner, string title, string prompt, string initial, string okText, bool required = false)
    {
        string? result = null;
        var window = NewWindow(owner, title, 420);
        var box = new TextBox { Text = initial, MaxLength = 500, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 10, 0, 0) };
        var (buttons, ok) = Buttons(okText, window, () => result = box.Text.Trim());
        void Update() => ok.IsEnabled = !required || !string.IsNullOrWhiteSpace(box.Text);
        box.TextChanged += (_, _) => Update();
        Update();
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children = { new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, FontSize = 14 }, box, buttons },
        };
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        window.ShowDialog();
        return required && string.IsNullOrWhiteSpace(result) ? null : result;
    }

    /// <summary>Режим «Касса»: включить и код кассы. null — отменили.</summary>
    public static (bool Enabled, string Code)? Settings(Window? owner, bool enabled, string code, string status)
    {
        (bool, string)? result = null;
        var window = NewWindow(owner, "Режим «Касса»", 480);
        TextBlock Hint(string text) => new()
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Muted, Margin = new Thickness(0, 6, 0, 0),
        };
        var enable = new CheckBox { Content = "Включить режим «Касса»", IsChecked = enabled, FontSize = 14 };
        var codeBox = new TextBox { Text = code, MaxLength = 100, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 4, 0, 0) };
        var (buttons, _) = Buttons("Сохранить", window, () => result = (enable.IsChecked == true, codeBox.Text.Trim()));
        var panel = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                enable,
                Hint("Слева появится список поставщиков с балансом: поставки (сколько по накладной и сколько выдали), " +
                     "возвраты и погашения. Излишки от округления копятся на балансе поставщика."),
                new TextBlock { Text = "Код кассы", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 0) },
                codeBox,
                Hint("Компьютеры с одинаковым кодом ведут общий журнал — по локальной сети, без сервера. Код — как пароль: " +
                     "без него чужие компьютеры записи не прочитают и не подделают. Без кода касса только на этом компьютере."),
            },
        };
        if (status.Length > 0) panel.Children.Add(Hint(status));
        panel.Children.Add(buttons);
        window.Content = panel;
        window.ShowDialog();
        return result;
    }

    /// <summary>Что ввели в окне «Сайт поставщика». Пустой логин — отключить сайт.</summary>
    public sealed record SiteLinkResult(string Login, string Password, DateTime Since, long? StartBalance);

    /// <summary>Подключить поставщика к сайту (Форум-Авто): логин, пароль, с какой даты учитывать, начальный баланс.</summary>
    public static SiteLinkResult? SiteLink(Window? owner, string login, bool linked, DateTime since)
    {
        SiteLinkResult? result = null;
        var window = NewWindow(owner, "Сайт поставщика", 460);
        TextBlock Label(string text) => new() { Text = text, FontSize = 12, Margin = new Thickness(0, 10, 0, 3) };
        TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Muted };
        Thickness pad = new(4, 3, 4, 3);
        var loginBox = new TextBox { Text = login, MaxLength = 100, Padding = pad };
        var passwordBox = new PasswordBox { MaxLength = 200, Padding = pad };
        var sinceBox = new TextBox { Text = since.ToString("dd.MM.yyyy"), Width = 140, Padding = pad, HorizontalAlignment = HorizontalAlignment.Left };
        var balanceBox = new TextBox { Width = 140, Padding = pad, HorizontalAlignment = HorizontalAlignment.Left };
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        // «Сохранить» — своя кнопка: окно закрывается, только если всё введено верно.
        var (buttons, _) = Buttons("Сохранить", window, () => { });
        var save = new Button { Content = "Сохранить", MinWidth = 100, Padding = new Thickness(10, 4, 10, 4), IsDefault = true };
        buttons.Children[0] = save;
        var off = new Button
        {
            Content = "Отключить", MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0),
            Visibility = linked ? Visibility.Visible : Visibility.Collapsed,
        };
        buttons.Children.Insert(0, off);
        save.Click += (_, _) =>
        {
            var name = loginBox.Text.Trim();
            long? balance = null;
            error.Visibility = Visibility.Visible;
            if (name.Length == 0) error.Text = "Введите логин";
            else if (!linked && passwordBox.Password.Length == 0) error.Text = "Введите пароль";
            else if (!DateTime.TryParseExact(sinceBox.Text.Trim(), "dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture,
                         System.Globalization.DateTimeStyles.None, out var date))
                error.Text = "Дата — в виде 20.11.2026";
            else if (!string.IsNullOrWhiteSpace(balanceBox.Text) && (balance = Models.Money.ParseSigned(balanceBox.Text)) == null)
                error.Text = "Начальный баланс — число, например 1500 или -250,50";
            else
            {
                result = new SiteLinkResult(name, passwordBox.Password, date, balance);
                window.Close();
            }
        };
        off.Click += (_, _) =>
        {
            result = new SiteLinkResult("", "", DateTime.Today, null);
            window.Close();
        };
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock { Text = "Форум-Авто (itrade.forum-auto.ru)", FontSize = 15, FontWeight = FontWeights.SemiBold },
                Hint("Касса сама будет заходить в личный кабинет: заказы из архива появятся в списке «ждут оплаты» " +
                     "(остаётся ввести, сколько отдали), а возвраты «Поставлен на баланс» сразу прибавятся к балансу."),
                Label("Логин"), loginBox,
                Label(linked ? "Пароль (оставьте пустым, чтобы не менять)" : "Пароль"), passwordBox,
                Hint("Пароль хранится в журнале кассы зашифрованным кодом кассы — им пользуются все компьютеры этой кассы."),
                Label("Учитывать заказы и возвраты с даты"), sinceBox,
                Label("Баланс поставщика на эту дату (необязательно)"), balanceBox,
                Hint("Чтобы начать учёт с известной суммы: баланс станет таким, дальше пойдут заказы и возвраты с сайта."),
                error,
                buttons,
            },
        };
        window.Loaded += (_, _) =>
        {
            if (login.Length == 0) loginBox.Focus();
            else passwordBox.Focus();
        };
        window.ShowDialog();
        return result;
    }

    /// <summary>Корректировка баланса: новая сумма и причина. null — отменили.</summary>
    public static (long Balance, string Reason)? Correction(Window? owner, string supplier, long current)
    {
        (long, string)? result = null;
        var window = NewWindow(owner, "Корректировка баланса", 420);
        Thickness pad = new(4, 3, 4, 3);
        var amount = new TextBox { Text = Models.Money.FormatInput(current), Width = 160, Padding = pad, HorizontalAlignment = HorizontalAlignment.Left };
        var reason = new TextBox { MaxLength = 500, Padding = pad };
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var (buttons, _) = Buttons("Записать", window, () => { });
        var save = new Button { Content = "Записать", MinWidth = 100, Padding = new Thickness(10, 4, 10, 4), IsDefault = true };
        buttons.Children[0] = save;
        save.Click += (_, _) =>
        {
            error.Visibility = Visibility.Visible;
            if (Models.Money.ParseSigned(amount.Text) is not { } balance) error.Text = "Баланс — число, например 1500 или -250,50";
            else if (string.IsNullOrWhiteSpace(reason.Text)) error.Text = "Укажите причину — она останется в истории";
            else
            {
                result = (balance, reason.Text.Trim());
                window.Close();
            }
        };
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock
                {
                    Text = $"{supplier}: сейчас {Models.Money.Format(current)}. С этого момента баланс станет таким, как укажете " +
                           "(запись с причиной останется в истории).",
                    TextWrapping = TextWrapping.Wrap, FontSize = 13,
                },
                new TextBlock { Text = "Новый баланс, ₽", FontSize = 12, Margin = new Thickness(0, 10, 0, 3) }, amount,
                new TextBlock { Text = "Причина (например: сверка с поставщиком)", FontSize = 12, Margin = new Thickness(0, 10, 0, 3) }, reason,
                error,
                buttons,
            },
        };
        window.Loaded += (_, _) =>
        {
            amount.Focus();
            amount.SelectAll();
        };
        window.ShowDialog();
        return result;
    }

    private static Window NewWindow(Window? owner, string title, double width) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        ResizeMode = ResizeMode.NoResize,
        ShowInTaskbar = false,
        Owner = owner is { IsVisible: true } ? owner : null,
        WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
    };

    private static (StackPanel Panel, Button Ok) Buttons(string okText, Window window, Action onOk)
    {
        var ok = new Button { Content = okText, MinWidth = 100, Padding = new Thickness(10, 4, 10, 4), IsDefault = true };
        var cancel = new Button
        {
            Content = "Отмена", MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), IsCancel = true, Margin = new Thickness(8, 0, 0, 0),
        };
        ok.Click += (_, _) =>
        {
            onOk();
            window.Close();
        };
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0),
            Children = { ok, cancel },
        };
        return (panel, ok);
    }
}
