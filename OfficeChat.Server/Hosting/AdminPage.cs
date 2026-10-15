using System.Reflection;

namespace OfficeChat.Server.Hosting;

/// <summary>
/// Страница администратора (https://сервер:45680/admin): файлы зашиты в программу, чтобы сервер оставался
/// одним файлом. Вход — тем же логином и паролем, что и в приложении (нужны права администратора).
/// </summary>
public static class AdminPage
{
    private static readonly Dictionary<string, string> ContentTypes = new()
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml",
    };

    public static void Map(WebApplication app)
    {
        app.MapGet("/", () => Results.Redirect("/admin/"));
        app.MapGet("/admin/{*file}", (string? file, HttpContext ctx) =>
        {
            // Без косой черты в конце относительные ссылки страницы не работают.
            if (string.IsNullOrEmpty(file) && !ctx.Request.Path.Value!.EndsWith('/')) return Results.Redirect("/admin/");
            file = string.IsNullOrEmpty(file) ? "index.html" : file;
            if (file.Contains("..") || !ContentTypes.TryGetValue(Path.GetExtension(file), out var type))
                return Results.NotFound();
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("admin/" + file);
            return stream == null ? Results.NotFound() : Results.Stream(stream, type);
        });
    }
}
