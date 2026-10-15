using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using OfficeChat.Client;
using OfficeChat.Protocol;
using OfficeChat.Server.Hosting;

namespace OfficeChat.Server.Tests;

/// <summary>Клиентская часть (OfficeChat.Core/Client) против настоящего сервера: HTTPS, WebSocket, переподключение.</summary>
public class ClientTests
{
    /// <summary>Настоящий сервер (Kestrel, HTTPS) на свободном порту; можно остановить и запустить снова.</summary>
    private sealed class RealServer : IAsyncDisposable
    {
        private WebApplication? _app;
        public ServerOptions Options { get; }
        public string Fingerprint { get; private set; } = "";

        public RealServer(bool discovery = false)
        {
            Options = new ServerOptions
            {
                DataFolder = Path.Combine(Path.GetTempPath(), "officechat-client-test-" + Guid.NewGuid().ToString("N")),
                Port = FreePort(),
                Discovery = discovery,
                Name = "Тестовый сервер",
            };
        }

        private static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public async Task StartAsync()
        {
            _app = ServerApp.Build(Options, Array.Empty<string>());
            await _app.StartAsync();
            Fingerprint = _app.Services.GetService(typeof(ServerIdentity)) is ServerIdentity id ? id.Fingerprint : "";
        }

        public async Task StopAsync()
        {
            if (_app == null) return;
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }

        public ServerApi Api(string? fingerprint = null, string? token = null) =>
            new("localhost", Options.Port, fingerprint ?? Fingerprint, token);

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Options.DataFolder, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static Task<AuthResponse> Register(ServerApi api, string login) =>
        api.RegisterAsync(new RegisterRequest(login, login, "secret1", new DeviceInfo("test", "tests")));

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Не дождались");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Api_pins_certificate_fingerprint()
    {
        await using var server = new RealServer();
        await server.StartAsync();

        using var api = server.Api();
        var auth = await Register(api, "alice");
        api.Token = auth.Token;
        Assert.Equal("alice", (await api.MeAsync()).Login);

        // Без отпечатка — принимаем первый увиденный и запоминаем
        using var firstTime = new ServerApi("localhost", server.Options.Port, null);
        await firstTime.InfoAsync();
        Assert.Equal(server.Fingerprint, firstTime.Fingerprint);

        // Чужой отпечаток — соединение не устанавливается
        using var impostor = server.Api(fingerprint: new string('A', 64));
        await Assert.ThrowsAsync<HttpRequestException>(() => impostor.InfoAsync());

        // Ошибки сервера приходят с понятным текстом
        var error = await Assert.ThrowsAsync<ServerApiException>(() => Register(api, "alice"));
        Assert.Equal(ErrorCodes.LoginTaken, error.Code);
        Assert.Equal("Такой логин уже занят", error.Message);
    }

    [Fact]
    public async Task Connection_syncs_and_survives_server_restart()
    {
        await using var server = new RealServer();
        await server.StartAsync();
        using var aliceApi = server.Api();
        using var borisApi = server.Api();
        var alice = await Register(aliceApi, "alice");
        var boris = await Register(borisApi, "boris");
        aliceApi.Token = alice.Token;
        borisApi.Token = boris.Token;

        // Борис пишет Алисе, пока она не подключена
        await using (var borisConnection = new ServerConnection(borisApi, () => 0, _ => Task.CompletedTask))
        {
            borisConnection.Start();
            await WaitUntil(() => borisConnection.IsConnected);
            await borisConnection.OkAsync(Ops.Send, new MessageDto
            {
                Id = Guid.NewGuid(), RecipientId = alice.Account.Id, Kind = MessageKinds.Text, Text = "первое",
            });
        }

        long lastSeq = 0;
        var texts = new List<string>();
        await using var aliceConnection = new ServerConnection(aliceApi, () => lastSeq, frame =>
        {
            if (frame.Kind == EventKinds.Message)
                texts.Add(frame.Data!.Value.GetProperty("text").GetString()!);
            lastSeq = frame.Seq!.Value;
            return Task.CompletedTask;
        });
        aliceConnection.Start();
        await WaitUntil(() => aliceConnection.IsConnected && texts.Count == 1);

        // Сервер перезапустили — соединение восстанавливается само, пропущенное догоняется без повторов
        await server.StopAsync();
        await WaitUntil(() => !aliceConnection.IsConnected);
        await server.StartAsync();
        await using (var borisAgain = new ServerConnection(borisApi, () => 0, _ => Task.CompletedTask))
        {
            borisAgain.Start();
            await WaitUntil(() => borisAgain.IsConnected);
            await borisAgain.OkAsync(Ops.Send, new MessageDto
            {
                Id = Guid.NewGuid(), RecipientId = alice.Account.Id, Kind = MessageKinds.Text, Text = "второе",
            });
        }
        await WaitUntil(() => aliceConnection.IsConnected && texts.Count == 2, 20000);
        Assert.Equal(new[] { "первое", "второе" }, texts);
    }

    [Fact]
    public async Task Plain_http_is_redirected_to_https()
    {
        await using var server = new RealServer();
        await server.StartAsync();
        // Адрес набрали без https:// — браузер идёт по http на тот же порт: должен получить перенаправление,
        // а не оборванное соединение («пустой ответ»).
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        using var response = await http.GetAsync($"http://localhost:{server.Options.Port}/admin/?x=1");
        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal($"https://localhost:{server.Options.Port}/admin/?x=1", response.Headers.Location?.ToString());

        // HTTPS на том же порту работает как раньше.
        using var api = server.Api();
        Assert.Equal("Тестовый сервер", (await api.InfoAsync()).Name);
    }

    [Fact]
    public async Task Revoked_device_gets_auth_failed()
    {
        await using var server = new RealServer();
        await server.StartAsync();
        using var api = server.Api();
        var auth = await Register(api, "alice");
        api.Token = auth.Token;

        string? reason = null;
        await using var connection = new ServerConnection(api, () => 0, _ => Task.CompletedTask);
        connection.AuthFailed += r => reason = r;
        connection.Start();
        await WaitUntil(() => connection.IsConnected);
        await api.LogoutAsync();
        await WaitUntil(() => reason != null, 20000);
    }

    [Fact]
    public async Task Upload_and_download_files()
    {
        await using var server = new RealServer();
        await server.StartAsync();
        using var api = server.Api();
        api.Token = (await Register(api, "alice")).Token;

        var source = Path.GetTempFileName();
        var target = Path.GetTempFileName();
        try
        {
            var content = new byte[2_500_000];
            Random.Shared.NextBytes(content);
            await File.WriteAllBytesAsync(source, content);
            // Без UI-потока Progress зовёт обработчик из разных потоков — копим потокобезопасно.
            var progress = new System.Collections.Concurrent.ConcurrentBag<double>();
            var uploaded = await api.UploadAsync(source, "отчёт.bin", new Progress<double>(progress.Add));
            Assert.Equal(content.Length, uploaded.Size);

            await api.DownloadAsync(uploaded.Id, target);
            Assert.Equal(content, await File.ReadAllBytesAsync(target));
        }
        finally
        {
            File.Delete(source);
            File.Delete(target);
        }
    }

    [Fact]
    public async Task Locator_finds_server_in_network()
    {
        await using var server = new RealServer(discovery: true);
        await server.StartAsync();
        var found = await ServerLocator.FindAsync(TimeSpan.FromSeconds(2));
        var mine = Assert.Single(found, f => f.Fingerprint == server.Fingerprint);
        Assert.Equal(server.Options.Port, mine.Port);
        Assert.Equal("Тестовый сервер", mine.Name);

        // Найденный адрес работает
        using var api = new ServerApi(mine.Host, mine.Port, mine.Fingerprint);
        Assert.Equal("Тестовый сервер", (await api.InfoAsync()).Name);
    }
}
