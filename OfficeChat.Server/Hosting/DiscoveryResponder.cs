using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OfficeChat.Protocol;

namespace OfficeChat.Server.Hosting;

/// <summary>
/// Отвечает на поиск сервера в локальной сети: клиент шлёт широковещательный UDP-запрос на порт 45681,
/// сервер отвечает именем, портом и отпечатком сертификата. Так приложения находят сервер без настройки.
/// </summary>
public sealed class DiscoveryResponder(ServerOptions options, ServerIdentity identity, ILogger<DiscoveryResponder> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Discovery) return;
        UdpClient udp;
        try
        {
            udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, ServerProtocol.DiscoveryPort));
        }
        catch (SocketException ex)
        {
            log.LogWarning(ex, "Поиск сервера в сети не работает: порт UDP {Port} занят", ServerProtocol.DiscoveryPort);
            return;
        }

        using var _ = udp;
        log.LogInformation("Отвечаем на поиск сервера в сети (UDP {Port})", ServerProtocol.DiscoveryPort);
        var reply = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new ServerAnnouncement(ServerProtocol.AppTag, "server",
            options.Name, options.Port, identity.Fingerprint, identity.Version), ServerProtocol.Json));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var request = await udp.ReceiveAsync(stoppingToken);
                if (!IsQuery(request.Buffer)) continue;
                await udp.SendAsync(reply, request.RemoteEndPoint, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex)
            {
                log.LogWarning(ex, "Ошибка при ответе на поиск сервера");
            }
        }
    }

    private static bool IsQuery(byte[] buffer)
    {
        try
        {
            using var doc = JsonDocument.Parse(buffer);
            var root = doc.RootElement;
            return root.TryGetProperty("app", out var app) && app.GetString() == ServerProtocol.AppTag &&
                   root.TryGetProperty("type", out var type) && type.GetString() == "server-query";
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Что сервер сообщает о себе: версия и отпечаток сертификата.</summary>
public sealed record ServerIdentity(string Version, string Fingerprint);
