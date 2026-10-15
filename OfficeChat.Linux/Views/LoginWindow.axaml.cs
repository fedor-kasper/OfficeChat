using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using OfficeChat.Client;
using OfficeChat.Protocol;

namespace OfficeChat.Views;

/// <summary>
/// Вход в аккаунт: сервер ищется в сети сам (или вводится адрес), затем вход или регистрация.
/// Если пароль временный (сбросил администратор) — сразу просим задать свой.
/// </summary>
public partial class LoginWindow : Window
{
    private readonly LoginFlow _flow = new();
    private bool _register;
    private bool _busy;
    private string _enteredPassword = "";

    /// <summary>Вошли: этот аккаунт (null — закрыли окно или выбрали работу без аккаунта).</summary>
    public SavedAccount? Account { get; private set; }

    /// <summary>Выбрали «работать без аккаунта».</summary>
    public bool ChoseNoAccount { get; private set; }

    public LoginWindow() : this(null, true) { }

    /// <param name="previous">Аккаунт, вход в который нужно повторить (логин и сервер уже известны).</param>
    /// <param name="allowNoAccount">Показывать «работать без аккаунта».</param>
    public LoginWindow(SavedAccount? previous, bool allowNoAccount)
    {
        InitializeComponent();
        Icon = Platform.AppIcon.Window;
        NoAccountButton.IsVisible = allowNoAccount;
        if (previous != null)
        {
            LoginBox.Text = previous.Login;
            SubtitleText.Text = "Вход больше не действует — войдите снова";
        }
        Opened += async (_, _) =>
        {
            (previous == null ? LoginBox : PasswordBox).Focus();
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
            Services.Log.Warn("Поиск сервера не удался", ex);
        }
        ShowServer("Сервер не найден в сети. Введите его адрес — его знает администратор.", "#F59E0B");
        AddressPanel.IsVisible = true;
        AddressBox.Focus();
    }

    private async Task ConnectToAsync(string address)
    {
        ShowServer($"Подключаемся к {address}…", "#9CA3AF");
        try
        {
            ShowFound(await _flow.ConnectAsync(address));
            AddressPanel.IsVisible = false;
        }
        catch (Exception ex)
        {
            ShowServer(LoginFlow.Describe(ex), "#DC2626");
            AddressPanel.IsVisible = true;
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
        ServerDot.Fill = SolidColorBrush.Parse(color);
    }

    private void ChangeServer_Click(object? sender, RoutedEventArgs e)
    {
        AddressPanel.IsVisible = !AddressPanel.IsVisible;
        if (AddressPanel.IsVisible) AddressBox.Focus();
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(AddressBox.Text)) await ConnectToAsync(AddressBox.Text);
    }

    // ---- Вход / регистрация ----

    private void SignInTab_Click(object? sender, RoutedEventArgs e) => SetMode(register: false);

    private void RegisterTab_Click(object? sender, RoutedEventArgs e) => SetMode(register: true);

    private void SetMode(bool register)
    {
        _register = register;
        SignInTab.IsChecked = !register;
        RegisterTab.IsChecked = register;
        NamePanel.IsVisible = register;
        RepeatPanel.IsVisible = register;
        SubmitButton.Content = register ? "Зарегистрироваться" : "Войти";
        ErrorText.IsVisible = false;
    }

    /// <summary>Временный пароль: остаётся задать свой.</summary>
    private void ShowChangePassword()
    {
        TabsGrid.IsVisible = false;
        LoginPanel.IsVisible = false;
        NamePanel.IsVisible = false;
        ChangePasswordHint.IsVisible = true;
        RepeatPanel.IsVisible = true;
        PasswordLabel.Text = "Новый пароль";
        PasswordBox.Text = RepeatBox.Text = "";
        SubmitButton.Content = "Сохранить пароль";
        NoAccountButton.IsVisible = false;
        PasswordBox.Focus();
    }

    private async void Submit_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ErrorText.IsVisible = false;
        if (_flow.Server == null)
        {
            ShowError("Сначала нужен сервер: дождитесь поиска или введите адрес.");
            return;
        }

        var login = LoginBox.Text?.Trim() ?? "";
        var password = PasswordBox.Text ?? "";
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
        if ((_register || changingPassword) && password != RepeatBox.Text)
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
                ? await _flow.RegisterAsync(login, NameBox.Text!.Trim(), password)
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
        ErrorText.IsVisible = true;
    }

    private void NoAccount_Click(object? sender, RoutedEventArgs e)
    {
        ChoseNoAccount = true;
        Account = null;
        Close();
    }
}
