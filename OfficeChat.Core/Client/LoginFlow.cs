using System.Security.Authentication;
using OfficeChat.Protocol;

namespace OfficeChat.Client;

/// <summary>
/// Вход и регистрация — общее для окна входа на Windows и Linux: найти сервер (или подключиться по адресу),
/// войти или зарегистрироваться, сменить временный пароль, запомнить аккаунт.
/// </summary>
public sealed class LoginFlow : IDisposable
{
    private ServerApi? _api;

    /// <summary>Сервер, с которым работаем (найден в сети или введён вручную).</summary>
    public FoundServer? Server { get; private set; }

    /// <summary>Вход выполнен с временным паролем — нужно задать свой.</summary>
    public bool MustChangePassword { get; private set; }

    /// <summary>Ищет сервер в сети; найденный становится текущим.</summary>
    public async Task<FoundServer?> SearchAsync(CancellationToken ct = default)
    {
        var found = await ServerLocator.FindAsync(TimeSpan.FromSeconds(3), ct);
        if (found.Count > 0) UseServer(found[0]);
        return Server;
    }

    /// <summary>Подключиться к серверу по адресу «host» или «host:port» (если в сети не нашёлся).</summary>
    public async Task<FoundServer> ConnectAsync(string address, CancellationToken ct = default)
    {
        var (host, port) = ParseAddress(address);
        // Отпечаток не знаем — запоминаем сертификат, который сервер покажет сейчас.
        using var api = new ServerApi(host, port, null);
        var info = await api.InfoAsync(ct);
        var server = new FoundServer(host, port, info.Name, api.Fingerprint ?? "", info.Version);
        UseServer(server);
        return server;
    }

    public static (string Host, int Port) ParseAddress(string address)
    {
        address = address.Trim();
        if (address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) address = address[8..];
        address = address.TrimEnd('/');
        var colon = address.LastIndexOf(':');
        if (colon > 0 && int.TryParse(address[(colon + 1)..], out var port) && port is > 0 and < 65536)
            return (address[..colon], port);
        if (address.Length == 0) throw new FormatException("Укажите адрес сервера");
        return (address, ServerProtocol.DefaultPort);
    }

    private void UseServer(FoundServer server)
    {
        Server = server;
        _api?.Dispose();
        _api = new ServerApi(server.Host, server.Port, server.Fingerprint);
    }

    private ServerApi Api => _api ?? throw new InvalidOperationException("Сервер не выбран");

    public async Task<SavedAccount> SignInAsync(string login, string password, CancellationToken ct = default)
    {
        var auth = await Api.LoginAsync(new LoginRequest(login.Trim(), password, AccountLauncher.ThisDevice()), ct);
        Api.Token = auth.Token;
        MustChangePassword = auth.Account.MustChangePassword;
        return AccountLauncher.Remember(Api, Server!.Name, auth);
    }

    public async Task<SavedAccount> RegisterAsync(string login, string displayName, string password, CancellationToken ct = default)
    {
        var auth = await Api.RegisterAsync(new RegisterRequest(login.Trim(), displayName.Trim(), password,
            AccountLauncher.ThisDevice()), ct);
        Api.Token = auth.Token;
        MustChangePassword = false;
        return AccountLauncher.Remember(Api, Server!.Name, auth);
    }

    public async Task ChangePasswordAsync(string oldPassword, string newPassword, CancellationToken ct = default)
    {
        await Api.ChangePasswordAsync(oldPassword, newPassword, ct);
        MustChangePassword = false;
    }

    /// <summary>Понятный человеку текст ошибки.</summary>
    public static string Describe(Exception ex) => ex switch
    {
        ServerApiException api => api.Message,
        HttpRequestException { InnerException: AuthenticationException } =>
            "Сертификат сервера не совпадает с запомненным. Если сервер переустанавливали — введите адрес заново; " +
            "иначе это может быть чужой компьютер, выдающий себя за сервер.",
        HttpRequestException => "Сервер не отвечает. Проверьте, что он включён и компьютер в той же сети.",
        TaskCanceledException => "Сервер не ответил вовремя.",
        FormatException => ex.Message,
        _ => ex.Message,
    };

    public void Dispose() => _api?.Dispose();
}
