using Avalonia.Interactivity;
using Avalonia.Media;
using OfficeChat.Client;
using OfficeChat.Protocol;
using OfficeChat.Services;

namespace OfficeChat.Mobile.Views;

/// <summary>
/// Вход на телефоне: сервер ищется в сети сам (или вводится адрес), затем вход или регистрация.
/// Временный пароль (сбросил администратор) сразу просим заменить своим.
/// </summary>
public partial class LoginPage : Page
{
    private readonly LoginFlow _flow = new();
    private readonly SavedAccount? _previous;
    private bool _register;
    private bool _busy;
    private string _enteredPassword = "";
    private SavedAccount? _account;

    public LoginPage() : this(null) { }

    /// <param name="previous">Аккаунт, вход в который нужно повторить.</param>
    /// <param name="reason">Почему вход нужен снова (показывается вверху).</param>
    public LoginPage(SavedAccount? previous, string? reason = null)
    {
        InitializeComponent();
        _previous = previous;
        if (previous != null)
        {
            LoginBox.Text = previous.Login;
            TabsGrid.IsVisible = false;
            SubtitleText.Text = (reason ?? "Вход больше не действует") + " — войдите снова";
        }
    }

    public override async void OnAppearing()
    {
        if (_flow.Server != null) return;
        if (_previous != null)
            await ConnectToAsync($"{_previous.ServerHost}:{_previous.ServerPort}");
        else
            await SearchAsync();
    }

    public override void OnDisappearing()
    {
        if (_account != null) _flow.Dispose();
    }

    // ---- Сервер ----

    private async Task SearchAsync()
    {
        ShowServer("Ищем сервер в сети…", "#9CA3AF");
        try
        {
            if (await _flow.SearchAsync() is { } server)
            {
                ShowFound(server);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Поиск сервера не удался", ex);
        }
        ShowServer("Сервер не найден в сети. Введите его адрес — его знает администратор. " +
                   "Телефон должен быть подключён к офисному Wi-Fi.", "#F59E0B");
        AddressPanel.IsVisible = true;
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

    private void ChangeServer_Click(object? sender, RoutedEventArgs e) => AddressPanel.IsVisible = !AddressPanel.IsVisible;

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
                Finish();
                return;
            }
            _account = _register
                ? await _flow.RegisterAsync(login, NameBox.Text!.Trim(), password)
                : await _flow.SignInAsync(login, password);
            if (_flow.MustChangePassword)
            {
                _enteredPassword = password;
                ShowChangePassword();
                return;
            }
            Finish();
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

    private void Finish()
    {
        if (_account == null) return;
        if (!MobileSession.Start(_account))
        {
            ShowError("Не удалось сохранить вход на этом телефоне");
            return;
        }
        Shell.SetRoot(new ChatsPage());
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.IsVisible = true;
    }
}
