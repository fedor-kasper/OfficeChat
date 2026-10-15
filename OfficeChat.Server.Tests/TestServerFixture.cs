using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using OfficeChat.Protocol;
using OfficeChat.Server.Data;
using OfficeChat.Server.Hosting;

namespace OfficeChat.Server.Tests;

/// <summary>Сервер в памяти со своей временной папкой данных — для каждого теста свой.</summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _counter;

    public string DataFolder { get; }
    public TestServer Server { get; }
    public ServerDb Db => _app.Services.GetRequiredService<ServerDb>();

    private TestHost(WebApplication app, string dataFolder)
    {
        _app = app;
        DataFolder = dataFolder;
        Server = app.GetTestServer();
    }

    public static async Task<TestHost> StartAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "officechat-server-test-" + Guid.NewGuid().ToString("N"));
        var options = new ServerOptions { DataFolder = folder, Discovery = false, Name = "test" };
        var app = ServerApp.Build(options, Array.Empty<string>(), b => b.WebHost.UseTestServer());
        await app.StartAsync();
        return new TestHost(app, folder);
    }

    public HttpClient Http(string? token = null)
    {
        var client = Server.CreateClient();
        if (token != null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Новый пользователь с одним устройством.</summary>
    public async Task<TestUser> RegisterAsync(string? login = null, string? name = null, string password = "secret1")
    {
        login ??= "user" + Interlocked.Increment(ref _counter);
        var response = await Http().PostAsJsonAsync("/api/register",
            new RegisterRequest(login, name ?? login, password, new DeviceInfo("test", "tests")), ServerProtocol.Json);
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(ServerProtocol.Json))!;
        return new TestUser(this, auth);
    }

    /// <summary>Ещё одно устройство того же пользователя.</summary>
    public async Task<TestUser> LoginAsync(string login, string password = "secret1")
    {
        var response = await Http().PostAsJsonAsync("/api/login",
            new LoginRequest(login, password, new DeviceInfo("second", "tests")), ServerProtocol.Json);
        response.EnsureSuccessStatusCode();
        return new TestUser(this, (await response.Content.ReadFromJsonAsync<AuthResponse>(ServerProtocol.Json))!);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(DataFolder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Устройство пользователя: HTTP и (по требованию) WebSocket.</summary>
public sealed class TestUser(TestHost host, AuthResponse auth) : IAsyncDisposable
{
    private WebSocket? _socket;
    private readonly List<Frame> _received = new();
    private int _rid;

    public AuthResponse Auth { get; } = auth;
    public Guid Id => Auth.Account.Id;
    public string Login => Auth.Account.Login;
    public HttpClient Http { get; } = host.Http(auth.Token);

    /// <summary>Все кадры, полученные по WebSocket (в порядке прихода).</summary>
    public IReadOnlyList<Frame> Received => _received;

    public async Task ConnectAsync(long since = 0)
    {
        var client = host.Server.CreateWebSocketClient();
        client.ConfigureRequest = r => r.Headers.Authorization = "Bearer " + Auth.Token;
        _socket = await client.ConnectAsync(new Uri(host.Server.BaseAddress, "api/ws"), CancellationToken.None);
        await SendFrameAsync(new Frame { Op = Ops.Sync, Since = since });
        await WaitForAsync(f => f.Op == Ops.Synced);
    }

    public async Task SendFrameAsync(Frame frame)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, ServerProtocol.Json);
        await _socket!.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    /// <summary>Запрос с ответом: возвращает ok или error.</summary>
    public async Task<Frame> RequestAsync(string op, object data)
    {
        var rid = "r" + Interlocked.Increment(ref _rid);
        await SendFrameAsync(new Frame { Op = op, Rid = rid, Data = JsonSerializer.SerializeToElement(data, ServerProtocol.Json) });
        return await WaitForAsync(f => f.Rid == rid);
    }

    public async Task<Frame> OkAsync(string op, object data)
    {
        var reply = await RequestAsync(op, data);
        Assert.True(reply.Op == Ops.Ok, $"{op}: {reply.Error} {reply.Message}");
        return reply;
    }

    public Task<Frame> EventAsync(string kind, Func<JsonElement, bool>? match = null) =>
        WaitForAsync(f => f.Op == Ops.Event && f.Kind == kind && (match == null || match(f.Data!.Value)));

    public IEnumerable<Frame> Events(string kind) => _received.Where(f => f.Op == Ops.Event && f.Kind == kind);

    /// <summary>Ждёт подходящий кадр (уже пришедший или новый).</summary>
    public async Task<Frame> WaitForAsync(Func<Frame, bool> match, int timeoutMs = 5000)
    {
        if (_received.FirstOrDefault(match) is { } existing) return existing;
        using var timeout = new CancellationTokenSource(timeoutMs);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            try
            {
                do
                {
                    result = await _socket!.ReceiveAsync(buffer, timeout.Token);
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Не дождались кадра. Получено: " +
                                           string.Join(", ", _received.Select(f => f.Op + "/" + f.Kind)));
            }
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("Сервер закрыл соединение");
            var frame = JsonSerializer.Deserialize<Frame>(Encoding.UTF8.GetString(message.ToArray()), ServerProtocol.Json)!;
            _received.Add(frame);
            if (match(frame)) return frame;
        }
    }

    /// <summary>Убедиться, что подходящий кадр НЕ приходит (ждём немного).</summary>
    public async Task AssertNoFrameAsync(Func<Frame, bool> match, int waitMs = 500)
    {
        try
        {
            var frame = await WaitForAsync(match, waitMs);
            Assert.Fail($"Не ожидали кадр {frame.Op}/{frame.Kind}");
        }
        catch (TimeoutException)
        {
        }
    }

    public async Task DisconnectAsync()
    {
        if (_socket == null) return;
        try
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
        {
            // Сервер уже закрыл соединение
        }
        _socket = null;
        _received.Clear();
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    public static MessageDto Text(Guid to, string text) => new()
    {
        Id = Guid.NewGuid(), RecipientId = to, Kind = MessageKinds.Text, Text = text, SentAt = DateTimeOffset.Now,
    };

    public static MessageDto GroupText(Guid group, string text) => new()
    {
        Id = Guid.NewGuid(), GroupId = group, Kind = MessageKinds.Text, Text = text, SentAt = DateTimeOffset.Now,
    };
}
