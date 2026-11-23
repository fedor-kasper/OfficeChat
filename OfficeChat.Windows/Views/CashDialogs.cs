using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OfficeChat.Services;

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
        var (buttons, ok) = Buttons(okText, window, () =>
        {
            result = box.Text.Trim();
            return true;
        });
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
        var (buttons, _) = Buttons("Сохранить", window, () =>
        {
            result = (enable.IsChecked == true, codeBox.Text.Trim());
            return true;
        });
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
    public sealed record SiteLinkResult(string Login, string Password, DateTime Since, long? StartBalance, string SiteId = ForumAutoSite.Id);

    /// <summary>Подключить поставщика к сайту (Форум-Авто): логин, пароль, с какой даты учитывать, начальный баланс.</summary>
    public static SiteLinkResult? SiteLink(Window? owner, string login, bool linked, DateTime since, string siteId = ForumAutoSite.Id)
    {
        SiteLinkResult? result = null;
        var window = NewWindow(owner, "Сайт поставщика", 460);
        TextBlock Label(string text) => new() { Text = text, FontSize = 12, Margin = new Thickness(0, 10, 0, 3) };
        TextBlock Hint(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Muted };
        Thickness pad = new(4, 3, 4, 3);
        var siteBox = new ComboBox { SelectedIndex = siteId == FavoritSite.Id ? 1 : 0, Padding = pad };
        siteBox.Items.Add(new ComboBoxItem { Content = "Форум-Авто (itrade.forum-auto.ru)", Tag = ForumAutoSite.Id });
        siteBox.Items.Add(new ComboBoxItem { Content = "Фаворит — наличный договор (lk.favorit-parts.ru)", Tag = FavoritSite.Id });
        siteBox.Items.Add(new ComboBoxItem { Content = "Авторусь (autorus.ru)", Tag = AutorusSite.Id });
        siteBox.SelectedIndex = siteId == AutorusSite.Id ? 2 : siteId == FavoritSite.Id ? 1 : 0;
        string SelectedSite() => (string)((ComboBoxItem)siteBox.SelectedItem).Tag;
        var loginBox = new TextBox { Text = login, MaxLength = 100, Padding = pad };
        var passwordBox = new PasswordBox { MaxLength = 200, Padding = pad };
        var sinceBox = new TextBox { Text = since.ToString("dd.MM.yyyy"), Width = 140, Padding = pad, HorizontalAlignment = HorizontalAlignment.Left };
        var balanceBox = new TextBox { Width = 140, Padding = pad, HorizontalAlignment = HorizontalAlignment.Left };
        var isDebt = new CheckBox { Content = "Это наш долг поставщику", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
        var currentBalance = new CheckBox
        {
            Content = new TextBlock { Text = "Сумма на сейчас — учитывать только новые документы", TextWrapping = TextWrapping.Wrap, Width = 370 }, IsChecked = true,
            Margin = new Thickness(0, 4, 0, 4),
            Visibility = siteId == FavoritSite.Id ? Visibility.Visible : Visibility.Collapsed,
        };
        siteBox.SelectionChanged += (_, _) => currentBalance.Visibility = SelectedSite() == FavoritSite.Id ? Visibility.Visible : Visibility.Collapsed;
        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        // «Сохранить» закрывает окно, только если всё введено верно.
        var (buttons, _) = Buttons("Сохранить", window, () =>
        {
            var name = loginBox.Text.Trim();
            long? balance = null;
            error.Visibility = Visibility.Visible;
            if (name.Length == 0) error.Text = "Введите логин";
            else if ((!linked || SelectedSite() != siteId) && passwordBox.Password.Length == 0) error.Text = "Введите пароль выбранного сайта";
            else if (!DateTime.TryParseExact(sinceBox.Text.Trim(), "dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture,
                         System.Globalization.DateTimeStyles.None, out var date))
                error.Text = "Дата — в виде 20.11.2026";
            else if (!string.IsNullOrWhiteSpace(balanceBox.Text) && (balance = Models.Money.ParseSigned(balanceBox.Text)) == null)
                error.Text = "Начальный баланс — число, например 1500 или -250,50";
            else
            {
                if (balance.HasValue && isDebt.IsChecked == true) balance = -Math.Abs(balance.Value);
                if (balance.HasValue && SelectedSite() == FavoritSite.Id && currentBalance.IsChecked == true) date = DateTime.Now;
                result = new SiteLinkResult(name, passwordBox.Password, date, balance, SelectedSite());
                return true;
            }
            return false;
        });
        var off = new Button
        {
            Content = "Отключить", MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0),
            Visibility = linked ? Visibility.Visible : Visibility.Collapsed,
        };
        buttons.Children.Insert(0, off);
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
                Label("Сайт поставщика"), siteBox,
                Hint("Для каждого сайта создайте отдельного поставщика. Отгрузки ждут ввода оплаты, возвраты пополняют баланс. " +
                     "У Фаворита учитывается только наличный договор б/н; оплаты сайта показываются для сверки. " +
                     "Авторусь: заказы до десяти подряд полученных; способ оплаты выбирается при записи."),
                Label("Логин"), loginBox,
                Label(linked ? "Пароль (оставьте пустым, чтобы не менять)" : "Пароль"), passwordBox,
                Hint("Пароль хранится в журнале кассы зашифрованным кодом кассы — им пользуются все компьютеры этой кассы."),
                Label("Учитывать заказы и возвраты с даты"), sinceBox,
                Label("Начальный баланс, ₽ (необязательно)"), balanceBox, isDebt, currentBalance,
                Hint("Укажите долг положительным числом с галочкой выше. Без галочки плюс означает, что поставщик должен нам. " +
                     "Если сумма не на сейчас — укажите баланс на начало выбранной даты. Возвраты начиная с неё прибавятся к балансу."),
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
        var (buttons, _) = Buttons("Записать", window, () =>
        {
            error.Visibility = Visibility.Visible;
            if (Models.Money.ParseSigned(amount.Text) is not { } balance) error.Text = "Баланс — число, например 1500 или -250,50";
            else if (string.IsNullOrWhiteSpace(reason.Text)) error.Text = "Укажите причину — она останется в истории";
            else
            {
                result = (balance, reason.Text.Trim());
                return true;
            }
            return false;
        });
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

    /// <summary>Кнопки «ОК» и «Отмена». <paramref name="onOk"/> — false, если введено не всё (окно остаётся открытым).</summary>
    private static (StackPanel Panel, Button Ok) Buttons(string okText, Window window, Func<bool> onOk)
    {
        var ok = new Button { Content = okText, MinWidth = 100, Padding = new Thickness(10, 4, 10, 4), IsDefault = true };
        var cancel = new Button
        {
            Content = "Отмена", MinWidth = 90, Padding = new Thickness(10, 4, 10, 4), IsCancel = true, Margin = new Thickness(8, 0, 0, 0),
        };
        ok.Click += (_, _) =>
        {
            if (onOk()) window.Close();
        };
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0),
            Children = { ok, cancel },
        };
        return (panel, ok);
    }
}
