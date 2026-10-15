using System.Reflection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using OfficeChat.Protocol;
using OfficeChat.Server.Api;
using OfficeChat.Server.Data;
using OfficeChat.Server.Realtime;
using OfficeChat.Server.Security;

namespace OfficeChat.Server.Hosting;

/// <summary>Сборка веб-приложения сервера: и для настоящего запуска, и для тестов.</summary>
public static class ServerApp
{
    public const string ServiceName = "OfficeChatServer";

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "1.0.0";

    /// <param name="configure">Для тестов: подменить хостинг (например, на TestServer).</param>
    public static WebApplication Build(ServerOptions options, string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        Directory.CreateDirectory(options.DataFolder);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Host.UseWindowsService(o => o.ServiceName = ServiceName);
        builder.Host.UseSystemd();
        builder.Logging.AddProvider(new FileLoggerProvider(options.LogsFolder));
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

        var certificate = ServerCertificate.LoadOrCreate(options.CertificatePath);
        var identity = new ServerIdentity(Version, ServerCertificate.Fingerprint(certificate));
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = 1024 * 1024; // файлы поднимают лимит у себя
            kestrel.ListenAnyIP(options.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(certificate);
            });
        });

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton(new ServerDb(options.DatabasePath));
        builder.Services.AddSingleton<Hub>();
        builder.Services.AddHostedService<RevocationWatcher>();
        builder.Services.AddSingleton<ChatLogic>();
        builder.Services.AddSingleton<LoginThrottle>();
        builder.Services.AddHostedService<DiscoveryResponder>();
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.PropertyNamingPolicy = ServerProtocol.Json.PropertyNamingPolicy;
            json.SerializerOptions.PropertyNameCaseInsensitive = true;
            json.SerializerOptions.Encoder = ServerProtocol.Json.Encoder;
        });
        configure?.Invoke(builder);

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        AdminPage.Map(app);
        Endpoints.Map(app);

        var log = app.Services.GetRequiredService<ILogger<Hub>>();
        app.Lifetime.ApplicationStarted.Register(() =>
        {
            log.LogInformation("OfficeChat Server {Version} запущен: порт {Port}, данные — {Folder}", Version, options.Port,
                options.DataFolder);
            log.LogInformation("Отпечаток сертификата: {Fingerprint}", FormatFingerprint(identity.Fingerprint));
        });
        return app;
    }

    /// <summary>«AB12CD…» → «AB:12:CD:…» — так удобнее сверять глазами.</summary>
    public static string FormatFingerprint(string hex) =>
        string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
}
