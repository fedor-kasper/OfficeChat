using Microsoft.AspNetCore.Http.Features;
using OfficeChat.Protocol;
using OfficeChat.Server.Data;
using OfficeChat.Server.Hosting;
using OfficeChat.Server.Realtime;
using OfficeChat.Server.Security;

namespace OfficeChat.Server.Api;

/// <summary>HTTP-запросы сервера: вход, аккаунты, устройства, файлы, администрирование, WebSocket.</summary>
public static class Endpoints
{
    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/server", (ServerOptions options, ServerIdentity identity) =>
            new ServerInfo(options.Name, identity.Version, ServerProtocol.Version));

        api.MapPost("/register", Register);
        api.MapPost("/login", Login);
        api.MapPost("/logout", Logout).RequireDevice();

        api.MapGet("/me", (HttpContext ctx) => ctx.Account().ToDto()).RequireDevice();
        api.MapPost("/me/password", ChangePassword).RequireDevice();
        api.MapPut("/me/profile", ChangeProfile).RequireDevice();

        api.MapGet("/devices", (HttpContext ctx, ServerDb db) =>
            db.Devices(ctx.Account().Id).Select(d =>
                new DeviceDto(d.Id, d.Name, d.Platform, d.CreatedAt, d.LastSeen, d.Id == ctx.Device().Id))).RequireDevice();
        api.MapDelete("/devices/{id:guid}", (Guid id, HttpContext ctx, ServerDb db, Hub hub) =>
        {
            if (!db.RevokeDevice(ctx.Account().Id, id)) return Auth.Error(404, ErrorCodes.NotFound, "Устройство не найдено");
            hub.Disconnect(ctx.Account().Id, id);
            return Results.NoContent();
        }).RequireDevice();

        api.MapGet("/accounts", (ServerDb db, Hub hub) =>
        {
            var online = hub.OnlineAccounts();
            return db.Accounts().Where(a => !a.IsBlocked)
                .Select(a => new DirectoryEntry(a.Id, a.Login, a.DisplayName, online.Contains(a.Id), a.LastSeen));
        }).RequireDevice();

        api.MapPost("/files", UploadFile).RequireDevice();
        api.MapGet("/files/{id:guid}", DownloadFile).RequireDevice();

        var admin = api.MapGroup("/admin").RequireAdmin();
        admin.MapGet("/accounts", (ServerDb db, Hub hub) =>
        {
            var online = hub.OnlineAccounts();
            return db.Accounts().Select(a => new AdminAccountDto(a.Id, a.Login, a.DisplayName, a.IsAdmin, a.IsBlocked,
                a.CreatedAt, a.LastSeen, db.Devices(a.Id).Count, online.Contains(a.Id)));
        });
        admin.MapPost("/accounts/{id:guid}/reset-password", AdminResetPassword);
        admin.MapPost("/accounts/{id:guid}/block", AdminBlock);
        admin.MapPost("/accounts/{id:guid}/admin", AdminSetAdmin);

        api.Map("/ws", WebSocketAsync);
    }

    // ---- Вход ----

    /// <summary>Логин: буквы, цифры, точка, дефис, подчёркивание; 3–32 символа.</summary>
    internal static string? CheckLogin(string login)
    {
        if (login.Length is < 3 or > 32) return "Логин — от 3 до 32 символов";
        return login.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
            ? null
            : "В логине можно использовать только буквы, цифры, точку, дефис и подчёркивание";
    }

    internal static string? CheckPassword(string password) =>
        password.Length < ServerProtocol.MinPasswordLength
            ? $"Пароль — не короче {ServerProtocol.MinPasswordLength} символов"
            : null;

    private static string CleanName(string? name, int max = ServerProtocol.MaxNameLength)
    {
        name = (name ?? "").Trim();
        return name.Length > max ? name[..max] : name;
    }

    private static AuthResponse IssueDevice(ServerDb db, Account account, DeviceInfo? device)
    {
        var token = Tokens.New();
        var deviceId = Guid.NewGuid();
        db.AddDevice(deviceId, account.Id, CleanName(device?.Name) is { Length: > 0 } n ? n : "Устройство",
            CleanName(device?.Platform, 32), Tokens.HashOf(token));
        return new AuthResponse(token, deviceId, account.ToDto());
    }

    private static IResult Register(RegisterRequest request, ServerDb db, Hub hub, ILogger<Hub> log)
    {
        var login = (request.Login ?? "").Trim();
        var name = CleanName(request.DisplayName);
        if (CheckLogin(login) is { } loginError) return Auth.Error(400, ErrorCodes.BadRequest, loginError);
        if (name.Length == 0) return Auth.Error(400, ErrorCodes.BadRequest, "Укажите имя — его увидят коллеги");
        if (CheckPassword(request.Password ?? "") is { } passwordError) return Auth.Error(400, ErrorCodes.BadRequest, passwordError);

        var id = Guid.NewGuid();
        if (!db.CreateAccount(id, login, name, Passwords.Hash(request.Password!)))
            return Auth.Error(409, ErrorCodes.LoginTaken, "Такой логин уже занят");
        var account = db.FindAccount(id)!;
        log.LogInformation("Зарегистрирован аккаунт {Login} ({Name})", login, name);
        hub.Broadcast(new Frame { Op = Ops.Directory, Account = new DirectoryEntry(id, login, name, false, null) });
        return Results.Json(IssueDevice(db, account, request.Device), ServerProtocol.Json);
    }

    private static IResult Login(LoginRequest request, HttpContext ctx, ServerDb db, LoginThrottle throttle, ILogger<Hub> log)
    {
        var login = (request.Login ?? "").Trim();
        var keys = new[] { "login:" + login.ToLowerInvariant(), "ip:" + ctx.Connection.RemoteIpAddress };
        if (throttle.IsLocked(keys))
            return Auth.Error(429, ErrorCodes.TooManyAttempts, "Слишком много неудачных попыток — подождите минуту");

        var account = db.FindAccountByLogin(login);
        if (account == null || !Passwords.Verify(request.Password ?? "", account.PasswordHash))
        {
            throttle.Failed(keys);
            log.LogWarning("Неудачный вход: {Login} с {Address}", login, ctx.Connection.RemoteIpAddress);
            return Auth.Error(401, ErrorCodes.BadCredentials, "Неверный логин или пароль");
        }
        throttle.Succeeded(keys);
        if (account.IsBlocked) return Auth.Error(403, ErrorCodes.Blocked, "Аккаунт заблокирован администратором");
        log.LogInformation("Вход: {Login}, устройство «{Device}»", account.Login, request.Device?.Name);
        return Results.Json(IssueDevice(db, account, request.Device), ServerProtocol.Json);
    }

    private static IResult Logout(HttpContext ctx, ServerDb db, Hub hub)
    {
        db.RevokeDevice(ctx.Account().Id, ctx.Device().Id);
        hub.Disconnect(ctx.Account().Id, ctx.Device().Id);
        return Results.NoContent();
    }

    private static IResult ChangePassword(ChangePasswordRequest request, HttpContext ctx, ServerDb db)
    {
        var account = ctx.Account();
        if (!Passwords.Verify(request.OldPassword ?? "", account.PasswordHash))
            return Auth.Error(400, ErrorCodes.BadCredentials, "Текущий пароль указан неверно");
        if (CheckPassword(request.NewPassword ?? "") is { } error) return Auth.Error(400, ErrorCodes.BadRequest, error);
        db.SetPassword(account.Id, Passwords.Hash(request.NewPassword!), mustChange: false);
        return Results.Json(db.FindAccount(account.Id)!.ToDto(), ServerProtocol.Json);
    }

    private static IResult ChangeProfile(ProfileRequest request, HttpContext ctx, ServerDb db, Hub hub)
    {
        var name = CleanName(request.DisplayName);
        if (name.Length == 0) return Auth.Error(400, ErrorCodes.BadRequest, "Укажите имя");
        var account = ctx.Account();
        db.SetDisplayName(account.Id, name);
        hub.Broadcast(new Frame
        {
            Op = Ops.Directory,
            Account = new DirectoryEntry(account.Id, account.Login, name, hub.IsOnline(account.Id), account.LastSeen),
        });
        return Results.Json(db.FindAccount(account.Id)!.ToDto(), ServerProtocol.Json);
    }

    // ---- Файлы ----

    private static async Task<IResult> UploadFile(HttpContext ctx, ServerDb db, ServerOptions options, string? name)
    {
        if (ctx.Request.ContentLength is > ServerProtocol.MaxFileSize)
            return Auth.Error(413, ErrorCodes.BadRequest, "Файл больше 1 ГБ");
        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = ServerProtocol.MaxFileSize;

        var fileName = CleanName(Path.GetFileName(name ?? ""), 255);
        if (fileName.Length == 0) fileName = "файл";
        var id = Guid.NewGuid();
        Directory.CreateDirectory(options.FilesFolder);
        var path = Path.Combine(options.FilesFolder, id.ToString("N"));
        var temp = path + ".part";
        long size;
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, true))
            {
                await ctx.Request.Body.CopyToAsync(file, ctx.RequestAborted);
                size = file.Length;
            }
            if (size > ServerProtocol.MaxFileSize) throw new IOException("Файл больше 1 ГБ");
            File.Move(temp, path);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or BadHttpRequestException)
        {
            File.Delete(temp);
            return Auth.Error(400, ErrorCodes.BadRequest, "Файл не загрузился: " + ex.Message);
        }
        db.AddFile(new StoredFile(id, ctx.Account().Id, fileName, size));
        return Results.Json(new FileUploadResponse(id, size), ServerProtocol.Json);
    }

    private static IResult DownloadFile(Guid id, HttpContext ctx, ServerDb db, ServerOptions options)
    {
        var file = db.FindFile(id);
        var path = Path.Combine(options.FilesFolder, id.ToString("N"));
        if (file == null || !File.Exists(path) || !db.CanReadFile(ctx.Account().Id, file))
            return Auth.Error(404, ErrorCodes.NotFound, "Файл не найден");
        return Results.File(path, "application/octet-stream", file.Name, enableRangeProcessing: true);
    }

    // ---- Администрирование ----

    private static IResult AdminResetPassword(Guid id, ServerDb db, Hub hub, ILogger<Hub> log, HttpContext ctx)
    {
        var account = db.FindAccount(id);
        if (account == null) return Auth.Error(404, ErrorCodes.NotFound, "Аккаунт не найден");
        var temporary = Passwords.Temporary();
        db.SetPassword(id, Passwords.Hash(temporary), mustChange: true);
        db.RevokeAllDevices(id);
        hub.Disconnect(id);
        log.LogInformation("{Admin} сбросил(а) пароль аккаунта {Login}", ctx.Account().Login, account.Login);
        return Results.Json(new ResetPasswordResponse(temporary), ServerProtocol.Json);
    }

    private static IResult AdminBlock(Guid id, BlockRequest request, ServerDb db, Hub hub, ILogger<Hub> log, HttpContext ctx)
    {
        var account = db.FindAccount(id);
        if (account == null) return Auth.Error(404, ErrorCodes.NotFound, "Аккаунт не найден");
        if (id == ctx.Account().Id) return Auth.Error(400, ErrorCodes.BadRequest, "Нельзя заблокировать самого себя");
        db.SetBlocked(id, request.Blocked);
        if (request.Blocked) hub.Disconnect(id);
        log.LogInformation("{Admin} {Action} аккаунт {Login}", ctx.Account().Login,
            request.Blocked ? "заблокировал(а)" : "разблокировал(а)", account.Login);
        return Results.NoContent();
    }

    private static IResult AdminSetAdmin(Guid id, AdminRequest request, ServerDb db, ILogger<Hub> log, HttpContext ctx)
    {
        var account = db.FindAccount(id);
        if (account == null) return Auth.Error(404, ErrorCodes.NotFound, "Аккаунт не найден");
        if (id == ctx.Account().Id && !request.IsAdmin)
            return Auth.Error(400, ErrorCodes.BadRequest, "Нельзя снять права администратора с самого себя");
        db.SetAdmin(id, request.IsAdmin);
        log.LogInformation("{Admin} {Action} права администратора: {Login}", ctx.Account().Login,
            request.IsAdmin ? "выдал(а)" : "снял(а)", account.Login);
        return Results.NoContent();
    }

    // ---- WebSocket ----

    private static async Task WebSocketAsync(HttpContext ctx, ServerDb db, Hub hub, ChatLogic logic,
        ILogger<ClientConnection> log, IHostApplicationLifetime lifetime)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            await Auth.Error(400, ErrorCodes.BadRequest, "Нужен WebSocket").ExecuteAsync(ctx);
            return;
        }
        if (Auth.Authenticate(ctx, db) is { } error)
        {
            await error.ExecuteAsync(ctx);
            return;
        }
        using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
        var connection = new ClientConnection(socket, ctx.Device(), ctx.Account(), db, logic, log);
        hub.Register(connection);
        try
        {
            await connection.RunAsync(lifetime.ApplicationStopping);
        }
        finally
        {
            hub.Unregister(connection);
        }
    }
}
