using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OfficeChat.Protocol;

namespace OfficeChat.Client;

/// <summary>Сервер ответил ошибкой: код (для логики) и понятный человеку текст.</summary>
public sealed class ServerApiException(HttpStatusCode status, string code, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>
/// HTTP-запросы к серверу. Доверяем только серверу с запомненным отпечатком сертификата
/// (его выдаёт поиск в сети или запоминаем при первом подключении по адресу).
/// </summary>
public sealed class ServerApi : IDisposable
{
    private readonly HttpClient _http;

    public string Host { get; }
    public int Port { get; }

    /// <summary>Ожидаемый отпечаток сертификата; null — примем первый увиденный (и запомним его).</summary>
    public string? Fingerprint { get; private set; }

    /// <summary>Ключ устройства (после входа).</summary>
    public string? Token
    {
        get => _token;
        set
        {
            _token = value;
            _http.DefaultRequestHeaders.Authorization = value == null ? null : new AuthenticationHeaderValue("Bearer", value);
        }
    }
    private string? _token;

    public Uri BaseUri => new UriBuilder("https", Host, Port).Uri;

    public ServerApi(string host, int port, string? fingerprint, string? token = null)
    {
        Host = host;
        Port = port;
        Fingerprint = string.IsNullOrEmpty(fingerprint) ? null : fingerprint;
        _http = new HttpClient(CreateHandler())
        {
            BaseAddress = BaseUri,
            Timeout = Timeout.InfiniteTimeSpan, // большие файлы; время ограничиваем сами
        };
        Token = token;
    }

    private SocketsHttpHandler CreateHandler() => new()
    {
        // Сервер в локальной сети: системный прокси (корпоративный) его не знает и только мешает.
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
        SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = ValidateCertificate },
    };

    /// <summary>Проверка сертификата сервера по отпечатку (не по цепочке — сертификат самоподписанный).</summary>
    public bool ValidateCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (certificate == null) return false;
        var actual = FingerprintOf(certificate);
        if (Fingerprint == null)
        {
            Fingerprint = actual;
            return true;
        }
        return string.Equals(actual, Fingerprint, StringComparison.OrdinalIgnoreCase);
    }

    public static string FingerprintOf(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    // ---- Запросы ----

    public Task<ServerInfo> InfoAsync(CancellationToken ct = default) => GetAsync<ServerInfo>("api/server", ct);

    public Task<AuthResponse> RegisterAsync(RegisterRequest request, CancellationToken ct = default) =>
        PostAsync<AuthResponse>("api/register", request, ct);

    public Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default) =>
        PostAsync<AuthResponse>("api/login", request, ct);

    public Task LogoutAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "api/logout", null, ct);

    public Task<AccountDto> MeAsync(CancellationToken ct = default) => GetAsync<AccountDto>("api/me", ct);

    public Task<AccountDto> ChangePasswordAsync(string oldPassword, string newPassword, CancellationToken ct = default) =>
        PostAsync<AccountDto>("api/me/password", new ChangePasswordRequest(oldPassword, newPassword), ct);

    public async Task<AccountDto> ChangeProfileAsync(string displayName, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Put, "api/me/profile", JsonContent.Create(new ProfileRequest(displayName),
            options: ServerProtocol.Json), ct);
        return (await response.Content.ReadFromJsonAsync<AccountDto>(ServerProtocol.Json, ct))!;
    }

    public Task<List<DirectoryEntry>> AccountsAsync(CancellationToken ct = default) =>
        GetAsync<List<DirectoryEntry>>("api/accounts", ct);

    public Task<List<DeviceDto>> DevicesAsync(CancellationToken ct = default) => GetAsync<List<DeviceDto>>("api/devices", ct);

    public Task RevokeDeviceAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Delete, $"api/devices/{id}", null, ct);

    /// <summary>Загрузить файл на сервер (потоком с диска) — для вложения в сообщение.</summary>
    public async Task<FileUploadResponse> UploadAsync(string path, string name, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, true);
        using var content = new ProgressStreamContent(file, progress);
        using var response = await SendAsync(HttpMethod.Post, "api/files?name=" + Uri.EscapeDataString(name), content, ct);
        return (await response.Content.ReadFromJsonAsync<FileUploadResponse>(ServerProtocol.Json, ct))!;
    }

    /// <summary>Скачать файл вложения в <paramref name="target"/> (сначала во временный файл рядом).</summary>
    public async Task DownloadAsync(Guid id, string target, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/files/{id}", null, ct, HttpCompletionOption.ResponseHeadersRead);
        var total = response.Content.Headers.ContentLength ?? 0;
        var temp = target + ".part";
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024, true))
            {
                var buffer = new byte[256 * 1024];
                long done = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) progress?.Report((double)done / total);
                }
            }
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, ct);
        return (await response.Content.ReadFromJsonAsync<T>(ServerProtocol.Json, ct))!;
    }

    private async Task<T> PostAsync<T>(string path, object body, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, path, JsonContent.Create(body, body.GetType(), options: ServerProtocol.Json), ct);
        return (await response.Content.ReadFromJsonAsync<T>(ServerProtocol.Json, ct))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        // Обычные запросы — не дольше 30 секунд; файлы — сколько нужно (их отменяет вызывающий).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (content is not ProgressStreamContent && completion == HttpCompletionOption.ResponseContentRead)
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var response = await _http.SendAsync(request, completion, timeout.Token);
        if (response.IsSuccessStatusCode) return response;

        using (response)
        {
            ErrorResponse? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<ErrorResponse>(ServerProtocol.Json, ct);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException or HttpRequestException)
            {
            }
            throw new ServerApiException(response.StatusCode, error?.Error ?? ErrorCodes.BadRequest,
                error?.Message ?? $"Сервер ответил ошибкой {(int)response.StatusCode}");
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Тело запроса из потока с отчётом о ходе отправки.</summary>
    private sealed class ProgressStreamContent(Stream stream, IProgress<double>? progress) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream target, TransportContext? context)
        {
            var buffer = new byte[256 * 1024];
            var total = stream.Length;
            long done = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0) progress?.Report((double)done / total);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = stream.Length;
            return true;
        }
    }
}
