using OfficeChat.Protocol;
using OfficeChat.Server.Data;
using OfficeChat.Server.Security;

namespace OfficeChat.Server.Api;

/// <summary>Кто пришёл: устройство по ключу из заголовка Authorization (или ?access_token= для WebSocket из браузера).</summary>
public static class Auth
{
    private const string DeviceKey = "officechat.device";
    private const string AccountKey = "officechat.account";

    public static Device Device(this HttpContext context) => (Device)context.Items[DeviceKey]!;

    public static Account Account(this HttpContext context) => (Account)context.Items[AccountKey]!;

    public static IResult Error(int status, string code, string message) =>
        Results.Json(new ErrorResponse(code, message), ServerProtocol.Json, statusCode: status);

    /// <summary>Проверяет ключ устройства. null — всё хорошо, иначе ответ с ошибкой.</summary>
    public static IResult? Authenticate(HttpContext context, ServerDb db)
    {
        var token = TokenOf(context);
        if (token == null) return Error(401, ErrorCodes.Unauthorized, "Нужно войти");
        var device = db.FindDeviceByToken(Tokens.HashOf(token));
        var account = device == null ? null : db.FindAccount(device.AccountId);
        if (device == null || account == null) return Error(401, ErrorCodes.Unauthorized, "Вход больше не действует — войдите снова");
        if (account.IsBlocked) return Error(403, ErrorCodes.Blocked, "Аккаунт заблокирован администратором");
        context.Items[DeviceKey] = device;
        context.Items[AccountKey] = account;
        return null;
    }

    private static string? TokenOf(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return header[7..].Trim();
        return context.Request.Query["access_token"].FirstOrDefault();
    }

    /// <summary>Только для вошедших.</summary>
    public static TBuilder RequireDevice<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
            Authenticate(ctx.HttpContext, ctx.HttpContext.RequestServices.GetRequiredService<ServerDb>()) ?? await next(ctx));

    /// <summary>Только для администраторов.</summary>
    public static TBuilder RequireAdmin<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var error = Authenticate(ctx.HttpContext, ctx.HttpContext.RequestServices.GetRequiredService<ServerDb>());
            if (error != null) return error;
            return ctx.HttpContext.Account().IsAdmin
                ? await next(ctx)
                : Error(403, ErrorCodes.Forbidden, "Нужны права администратора");
        });
}
