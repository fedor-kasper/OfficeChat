using System.Windows;
using System.Windows.Media;
using OfficeChat.Client;
using OfficeChat.Protocol;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>
/// Вход в аккаунт: сервер ищется в сети сам (или вводится адрес), затем вход или регистрация.
/// Если пароль временный (сбросил администратор) — сразу просим задать свой.
/// </summary>
public partial class LoginWindow : Window
{
    private static readonly Brush ActiveTab = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly Brush InactiveTab = new SolidColorBrush(Color.FromRgb(0xF3, 0xF5, 0xF8));

    private readonly LoginFlow _flow = new();
    private bool _register;
    private bool _busy;
    private string _enteredPassword = "";

    /// <summary>Вошли: этот аккаунт (null — закрыли окно или выбрали работу без аккаунта).</summary>
    public SavedAccount? Account { get; private set; }

    /// <summary>Выбрали «работать без аккаунта».</summary>
    public bool ChoseNoAccount { get; private set; }

    /// <param name="previous">Аккаунт, вход в который нужно повторить (логин и сервер уже известны).</param>
    /// <param name="allowNoAccount">Показывать «работать без аккаунта».</param>
    public LoginWindow(SavedAccount? previous, bool allowNoAccount)
    {
        InitializeComponent();
        Icon = AppIcon.CreateImageSource();
        NoAccountButton.Visibility = allowNoAccount ? Visibility.Visible : Visibility.Collapsed;
        SetMode(register: false);
        if (previous != null)
        {
            LoginBox.Text = previous.Login;
            TabsGrid.Visibility = Visibility.Collapsed;
            SubtitleText.Text = "Вход больше не действует — войдите снова";
        }
        Loaded += async (_, _) =>
        {
            if (previous == null) LoginBox.Focus();
            else PasswordBox.Focus();
            if (previous != null)
                await ConnectToAsync($"{previous.ServerHost}:{previous.ServerPort}");
            else
                await SearchAsync();
        };
        Closed += (_, _) => _flow.Dispose();
    }

    // ---- Сервер ----

    private async Task SearchAsync()
    {
        ShowServer("Ищем сервер в сети…", "#9CA3AF");
        try
        {
            var server = await _flow.SearchAsync();
            if (server != null)
            {
                ShowFound(server);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Поиск сервера не удался", ex);
        }
        ShowServer("Сервер не найден в сети. Введите его адрес — его знает администратор.", "#F59E0B");
        AddressPanel.Visibility = Visibility.Visible;
        AddressBox.Focus();
    }

    private async Task ConnectToAsync(string address)
    {
        ShowServer($"Подключаемся к {address}…", "#9CA3AF");
        try
        {
            ShowFound(await _flow.ConnectAsync(address));
            AddressPanel.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ShowServer(LoginFlow.Describe(ex), "#DC2626");
            AddressPanel.Visibility = Visibility.Visible;
            AddressBox.Text = address;
        }
    }

    private void ShowFound(FoundServer server)
    {
        var name = server.Name.Length > 0 ? server.Name : "Сервер";
        ShowServer($"{name} · {server.Host}" + (server.Port != ServerProtocol.DefaultPort ? $":{server.Port}" : ""), "#22C55E");
    }

    private void ShowServer(string text, string color)
    {
        ServerText.Text = text;
        ServerDot.Fill = (Brush)new BrushConverter().ConvertFromString(color)!;
    }

    private void ChangeServer_Click(object sender, RoutedEventArgs e)
    {
        var show = AddressPanel.Visibility != Visibility.Visible;
        AddressPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) AddressBox.Focus();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(AddressBox.Text)) await ConnectToAsync(AddressBox.Text);
    }

    // ---- Вход / регистрация ----

    private void SignInTab_Click(object sender, RoutedEventArgs e) => SetMode(register: false);

    private void RegisterTab_Click(object sender, RoutedEventArgs e) => SetMode(register: true);

    private void SetMode(bool register)
    {
        _register = register;
        SignInTab.Background = register ? InactiveTab : ActiveTab;
        SignInTab.Foreground = register ? Brushes.Black : Brushes.White;
        RegisterTab.Background = register ? ActiveTab : InactiveTab;
        RegisterTab.Foreground = register ? Brushes.White : Brushes.Black;
        NamePanel.Visibility = register ? Visibility.Visible : Visibility.Collapsed;
        RepeatPanel.Visibility = register ? Visibility.Visible : Visibility.Collapsed;
        SubmitButton.Content = register ? "Зарегистрироваться" : "Войти";
        ErrorText.Visibility = Visibility.Collapsed;
    }

    /// <summary>Временный пароль: остаётся задать свой.</summary>
    private void ShowChangePassword()
    {
        TabsGrid.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Collapsed;
        NamePanel.Visibility = Visibility.Collapsed;
        ChangePasswordHint.Visibility = Visibility.Visible;
        RepeatPanel.Visibility = Visibility.Visible;
        PasswordLabel.Text = "Новый пароль";
        PasswordBox.Password = RepeatBox.Password = "";
        SubmitButton.Content = "Сохранить пароль";
        NoAccountButton.Visibility = Visibility.Collapsed;
        PasswordBox.Focus();
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ErrorText.Visibility = Visibility.Collapsed;
        if (_flow.Server == null)
        {
            ShowError("Сначала нужен сервер: дождитесь поиска или введите адрес.");
            return;
        }

        var login = LoginBox.Text.Trim();
        var password = PasswordBox.Password;
        var changingPassword = _flow.MustChangePassword;
        if (!changingPassword && login.Length == 0)
        {
            ShowError("Введите логин");
            return;
        }
        if (password.Length < ServerProtocol.MinPasswordLength && (_register || changingPassword))
        {
            ShowError($"Пароль — не короче {ServerProtocol.MinPasswordLength} символов");
            return;
        }
        if ((_register || changingPassword) && password != RepeatBox.Password)
        {
            ShowError("Пароли не совпадают");
            return;
        }
        if (_register && string.IsNullOrWhiteSpace(NameBox.Text))
        {
            ShowError("Укажите имя — его увидят коллеги");
            return;
        }

        _busy = true;
        SubmitButton.IsEnabled = false;
        try
        {
            if (changingPassword)
            {
                await _flow.ChangePasswordAsync(_enteredPassword, password);
                Close();
                return;
            }
            Account = _register
                ? await _flow.RegisterAsync(login, NameBox.Text.Trim(), password)
                : await _flow.SignInAsync(login, password);
            if (_flow.MustChangePassword)
            {
                _enteredPassword = password;
                ShowChangePassword();
                return;
            }
            Close();
        }
        catch (Exception ex)
        {
            ShowError(LoginFlow.Describe(ex));
        }
        finally
        {
            _busy = false;
            SubmitButton.IsEnabled = true;
        }
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void NoAccount_Click(object sender, RoutedEventArgs e)
    {
        ChoseNoAccount = true;
        Account = null;
        Close();
    }
}
