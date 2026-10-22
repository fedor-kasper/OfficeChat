using System.Net;
using System.Net.Http.Json;
using OfficeChat.Protocol;

namespace OfficeChat.Server.Tests;

public class AccountTests
{
    [Fact]
    public async Task Register_login_and_me()
    {
        await using var host = await TestHost.StartAsync();
        var fedor = await host.RegisterAsync("fedor", "Фёдор");
        Assert.Equal("Фёдор", fedor.Auth.Account.DisplayName);

        var me = await fedor.Http.GetFromJsonAsync<AccountDto>("/api/me", ServerProtocol.Json);
        Assert.Equal(fedor.Id, me!.Id);

        // Вход с другого устройства — тот же аккаунт, новый ключ; логин без учёта регистра
        var second = await host.LoginAsync("FEDOR");
        Assert.Equal(fedor.Id, second.Id);
        Assert.NotEqual(fedor.Auth.Token, second.Auth.Token);
        var devices = await second.Http.GetFromJsonAsync<List<DeviceDto>>("/api/devices", ServerProtocol.Json);
        Assert.Equal(2, devices!.Count);
        Assert.Single(devices, d => d.Current);
    }

    [Fact]
    public async Task Duplicate_login_and_bad_input_are_rejected()
    {
        await using var host = await TestHost.StartAsync();
        await host.RegisterAsync("anna");
        var http = host.Http();

        var taken = await http.PostAsJsonAsync("/api/register",
            new RegisterRequest("Anna", "Анна", "secret1", new DeviceInfo("pc", "w")), ServerProtocol.Json);
        Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
        Assert.Equal(ErrorCodes.LoginTaken, (await taken.Content.ReadFromJsonAsync<ErrorResponse>(ServerProtocol.Json))!.Error);

        var shortPassword = await http.PostAsJsonAsync("/api/register",
            new RegisterRequest("boris", "Борис", "123", new DeviceInfo("pc", "w")), ServerProtocol.Json);
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);

        var badLogin = await http.PostAsJsonAsync("/api/register",
            new RegisterRequest("bo ris", "Борис", "secret1", new DeviceInfo("pc", "w")), ServerProtocol.Json);
        Assert.Equal(HttpStatusCode.BadRequest, badLogin.StatusCode);

        // Кириллический логин допустим
        var cyrillic = await http.PostAsJsonAsync("/api/register",
            new RegisterRequest("борис", "Борис", "secret1", new DeviceInfo("pc", "w")), ServerProtocol.Json);
        Assert.Equal(HttpStatusCode.OK, cyrillic.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_throttled()
    {
        await using var host = await TestHost.StartAsync();
        await host.RegisterAsync("viktor");
        var http = host.Http();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
            last = await http.PostAsJsonAsync("/api/login", new LoginRequest("viktor", "wrong!!", new DeviceInfo("pc", "w")),
                ServerProtocol.Json);
        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        // Даже верный пароль не помогает, пока действует пауза
        var correct = await http.PostAsJsonAsync("/api/login", new LoginRequest("viktor", "secret1", new DeviceInfo("pc", "w")),
            ServerProtocol.Json);
        Assert.Equal(HttpStatusCode.TooManyRequests, correct.StatusCode);
    }

    [Fact]
    public async Task Logout_and_revoked_device_lose_access()
    {
        await using var host = await TestHost.StartAsync();
        var user = await host.RegisterAsync("gleb");
        var phone = await host.LoginAsync("gleb");

        // Отключили телефон с компьютера
        var revoke = await user.Http.DeleteAsync($"/api/devices/{phone.Auth.DeviceId}");
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.Http.GetAsync("/api/me")).StatusCode);

        // Вышли на компьютере
        await user.Http.PostAsync("/api/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.Http.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task Change_password_and_profile()
    {
        await using var host = await TestHost.StartAsync();
        var user = await host.RegisterAsync("dasha", "Даша");
        var wrong = await user.Http.PostAsJsonAsync("/api/me/password", new ChangePasswordRequest("nope", "newpass1"), ServerProtocol.Json);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var ok = await user.Http.PostAsJsonAsync("/api/me/password", new ChangePasswordRequest("secret1", "newpass1"), ServerProtocol.Json);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        await host.LoginAsync("dasha", "newpass1");

        await user.Http.PutAsJsonAsync("/api/me/profile", new ProfileRequest("Дарья"), ServerProtocol.Json);
        var directory = await user.Http.GetFromJsonAsync<List<DirectoryEntry>>("/api/accounts", ServerProtocol.Json);
        Assert.Contains(directory!, a => a.Login == "dasha" && a.DisplayName == "Дарья");
    }

    [Fact]
    public async Task Admin_rights_reset_password_and_block()
    {
        await using var host = await TestHost.StartAsync();
        var admin = await host.RegisterAsync("fedor");
        var worker = await host.RegisterAsync("ivan");

        // Пока не администратор — нельзя
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Http.GetAsync("/api/admin/accounts")).StatusCode);
        host.Db.SetAdmin(admin.Id, true); // так делает команда «officechat-server admin fedor»

        var list = await admin.Http.GetFromJsonAsync<List<AdminAccountDto>>("/api/admin/accounts", ServerProtocol.Json);
        Assert.Equal(2, list!.Count);

        // Сброс пароля: старый ключ перестаёт действовать, временный пароль работает и просит сменить
        var reset = await admin.Http.PostAsync($"/api/admin/accounts/{worker.Id}/reset-password", null);
        var temporary = (await reset.Content.ReadFromJsonAsync<ResetPasswordResponse>(ServerProtocol.Json))!.TemporaryPassword;
        Assert.Equal(HttpStatusCode.Unauthorized, (await worker.Http.GetAsync("/api/me")).StatusCode);
        var relogin = await host.LoginAsync("ivan", temporary);
        Assert.True(relogin.Auth.Account.MustChangePassword);

        // Блокировка: войти нельзя, ключ не действует
        await admin.Http.PostAsJsonAsync($"/api/admin/accounts/{worker.Id}/block", new BlockRequest(true), ServerProtocol.Json);
        Assert.Equal(HttpStatusCode.Forbidden, (await relogin.Http.GetAsync("/api/me")).StatusCode);
        var blockedLogin = await host.Http().PostAsJsonAsync("/api/login",
            new LoginRequest("ivan", temporary, new DeviceInfo("pc", "w")), ServerProtocol.Json);
        Assert.Equal(HttpStatusCode.Forbidden, blockedLogin.StatusCode);

        // Выдать права другому
        await admin.Http.PostAsJsonAsync($"/api/admin/accounts/{worker.Id}/admin", new AdminRequest(true), ServerProtocol.Json);
        Assert.True(host.Db.FindAccount(worker.Id)!.IsAdmin);
    }

    [Fact]
    public async Task Admin_page_is_served()
    {
        await using var host = await TestHost.StartAsync();
        var page = await host.Http().GetAsync("/admin/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("OfficeChat", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Server_update_is_admin_only_and_needs_signed_build()
    {
        await using var host = await TestHost.StartAsync();
        var admin = await host.RegisterAsync("fedor");
        var junk = new ByteArrayContent(new byte[1000]);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.Http.PostAsync("/api/admin/server/update", junk)).StatusCode);
        host.Db.SetAdmin(admin.Id, true);

        var info = await admin.Http.GetFromJsonAsync<AdminServerDto>("/api/admin/server", ServerProtocol.Json);
        Assert.Equal(OfficeChat.Server.Hosting.ServerUpdater.Platform, info!.Platform);

        // Тесты идут через dotnet, а не из собранного файла: обновлять нечего — и ничего не подменяется.
        var result = await admin.Http.PostAsync("/api/admin/server/update", new ByteArrayContent(new byte[1000]));
        var update = await result.Content.ReadFromJsonAsync<OfficeChat.Server.Hosting.UpdateResult>(ServerProtocol.Json);
        Assert.False(update!.Ok);
        Assert.False(info.CanUpdate);
    }
}
