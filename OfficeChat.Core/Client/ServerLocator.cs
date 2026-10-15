using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OfficeChat.Protocol;
using OfficeChat.Services;

namespace OfficeChat.Client;

/// <summary>Найденный в сети сервер: где он и какой у него сертификат.</summary>
public sealed record FoundServer(string Host, int Port, string Name, string Fingerprint, string Version);

/// <summary>
/// Поиск сервера в локальной сети: широковещательный UDP-запрос (на общий адрес и на адреса всех подсетей),
/// сервер отвечает адресом, портом и отпечатком сертификата.
/// </summary>
public static class ServerLocator
{
    public static async Task<List<FoundServer>> FindAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var found = new Dictionary<string, FoundServer>();
        // Свой порт сразу: ответы ждём параллельно с отправкой запросов.
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        var query = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { app = ServerProtocol.AppTag, type = "server-query" }));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        // UDP может потеряться — повторяем запрос несколько раз, пока ждём ответы.
        var sender = Task.Run(async () =>
        {
            try
            {
                for (var round = 0; round < 3 && !deadline.IsCancellationRequested; round++)
                {
                    foreach (var address in BroadcastAddresses())
                    {
                        try
                        {
                            await udp.SendAsync(query, new IPEndPoint(address, ServerProtocol.DiscoveryPort), deadline.Token);
                        }
                        catch (SocketException)
                        {
                            // Подсеть недоступна — пробуем остальные.
                        }
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(400), deadline.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });

        try
        {
            while (true)
            {
                var reply = await udp.ReceiveAsync(deadline.Token);
                if (Parse(reply.Buffer, reply.RemoteEndPoint.Address) is { } server)
                    found.TryAdd(server.Fingerprint + "|" + server.Host, server);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException ex)
        {
            Log.Warn("Поиск сервера в сети не удался", ex);
        }
        await sender;
        // Один сервер может ответить с нескольких адресов — оставляем по одному на сертификат.
        return found.Values.GroupBy(s => s.Fingerprint).Select(g => g.First()).ToList();
    }

    private static FoundServer? Parse(byte[] buffer, IPAddress from)
    {
        try
        {
            var reply = JsonSerializer.Deserialize<ServerAnnouncement>(buffer, ServerProtocol.Json);
            if (reply is not { App: ServerProtocol.AppTag, Type: "server" } || reply.Fingerprint.Length == 0) return null;
            return new FoundServer(from.MapToIPv4().ToString(), reply.Port, reply.Name, reply.Fingerprint, reply.Version);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ip = unicast.Address.GetAddressBytes();
                    var mask = unicast.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (var i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    addresses.Add(new IPAddress(broadcast));
                }
            }
        }
        catch (NetworkInformationException)
        {
        }
        foreach (var address in addresses.Distinct()) yield return address;
    }
}
