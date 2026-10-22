using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using OfficeChat.Services;

namespace OfficeChat.Server.Hosting;

/// <summary>
/// Сервер обновляет себя сам: раз в несколько часов смотрит последний выпуск на GitHub, а ещё новую версию
/// можно загрузить на странице администратора. Принимается только сборка с подписью из GitHub Actions
/// (та же подпись, что у программ, но для «server-…»), и только более новая. Затем файл программы
/// подменяется и сервер перезапускается: под systemd — сам по коду выхода (Restart=on-failure), служба Windows
/// и запуск из консоли — новой версией, которая дожидается выхода старой и встаёт на её место.
/// </summary>
public sealed class ServerUpdater : BackgroundService
{
    /// <summary>Откуда брать выпуски.</summary>
    public const string ReleasesUrl = "https://api.github.com/repos/fedor-kasper/OfficeChat/releases/latest";

    /// <summary>Код выхода «перезапусти меня» для systemd (Restart=on-failure).</summary>
    public const int RestartExitCode = 75;

    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly ServerOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ServerUpdater> _log;
    private readonly SemaphoreSlim _busy = new(1, 1);
    // Уже проверенный файл выпуска (чтобы не скачивать его заново каждые 6 часов).
    private string? _checkedAsset;
    // Проверенная новая версия рядом с программой (officechat-server.new), ждёт остановки сервера.
    private static string? _staged;

    /// <summary>Сервер остановлен ради обновления: Main возвращает <see cref="RestartExitCode"/>.</summary>
    public static bool RestartRequested { get; private set; }

    /// <summary>Подпись сборки сервера для этой системы.</summary>
    public static string Platform => "server-" + (OperatingSystem.IsWindows() ? "win-x64" : "linux-x64");

    /// <summary>Имя файла сервера в выпуске на GitHub.</summary>
    public static string AssetName =>
        OperatingSystem.IsWindows() ? "officechat-server-windows-x64.exe" : "officechat-server-linux-x64";

    public static Version CurrentVersion => Version.TryParse(ServerApp.Version, out var v) ? v : new Version(1, 0, 0);

    /// <summary>Файл работающей программы (null — запущен не из собранного файла, а через dotnet).</summary>
    public static string? ProgramPath =>
        Environment.ProcessPath is { } path && Path.GetFileNameWithoutExtension(path) != "dotnet" ? path : null;

    /// <summary>Чем закончилась последняя проверка (для страницы администратора).</summary>
    public string LastCheck { get; private set; } = "";

    public ServerUpdater(ServerOptions options, IHostApplicationLifetime lifetime, ILogger<ServerUpdater> log)
    {
        _options = options;
        _lifetime = lifetime;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Остатки прошлого обновления — когда вспомогательный процесс точно завершился.
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            CleanUp();
            if (!_options.AutoUpdate || ProgramPath == null) return;
            await Task.Delay(FirstCheckDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckAsync(stoppingToken);
                await Task.Delay(CheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Посмотреть последний выпуск на GitHub и, если там новее, обновиться.</summary>
    public async Task<UpdateResult> CheckAsync(CancellationToken cancel)
    {
        if (ProgramPath == null) return Done(UpdateResult.Fail("Сервер запущен не из собранного файла — обновлять нечего"));
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OfficeChat-Server", ServerApp.Version));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var release = JsonDocument.Parse(await http.GetStringAsync(ReleasesUrl, cancel));
            var asset = release.RootElement.GetProperty("assets").EnumerateArray()
                .FirstOrDefault(a => a.GetProperty("name").GetString() == AssetName);
            if (asset.ValueKind != JsonValueKind.Object)
                return Done(UpdateResult.Fail("В последнем выпуске нет сервера для этой системы"));
            var key = $"{asset.GetProperty("id").GetInt64()}:{asset.GetProperty("updated_at").GetString()}";
            if (key == _checkedAsset) return Done(UpdateResult.Fail($"Новее нет (установлена {ServerApp.Version})"));

            var url = asset.GetProperty("browser_download_url").GetString()!;
            await using var stream = await http.GetStreamAsync(url, cancel);
            var result = await InstallAsync(stream, cancel);
            if (!result.Ok || result.Version == null) _checkedAsset = key;
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException
                                       or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            _log.LogInformation("Проверка обновлений сервера не удалась: {Error}", ex.Message);
            return Done(UpdateResult.Fail("Не удалось проверить обновления: " + ex.Message));
        }
    }

    /// <summary>
    /// Принять новую сборку сервера (загрузили на странице администратора или скачали с GitHub): проверить подпись
    /// и версию, подменить файл программы и перезапуститься.
    /// </summary>
    public async Task<UpdateResult> InstallAsync(Stream content, CancellationToken cancel)
    {
        if (ProgramPath is not { } program)
            return Done(UpdateResult.Fail("Сервер запущен не из собранного файла — обновлять нечего"));
        if (_staged != null) return Done(UpdateResult.Fail("Новая версия уже устанавливается"));
        if (!await _busy.WaitAsync(0, cancel)) return Done(UpdateResult.Fail("Обновление уже идёт"));
        var staged = Path.Combine(Path.GetDirectoryName(program)!, Path.GetFileName(program) + ".new");
        try
        {
            await using (var file = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None))
                await content.CopyToAsync(file, cancel);

            var package = UpdateService.Verify(staged);
            if (package == null) return Fail("Файл не подписан или подпись неверна — это не сборка OfficeChat Server");
            if (package.Platform != Platform)
                return Fail($"Это сборка для «{package.Platform}», а нужна для «{Platform}»");
            if (package.Version <= CurrentVersion)
            {
                File.Delete(staged);
                return Done(new UpdateResult(true, $"Новее нет: установлена {ServerApp.Version}, в файле {package.Version}", null));
            }

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(staged, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                             UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                             UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            // Сам файл программы подменяем только после остановки: сервер собран одним файлом и по ходу работы
            // подгружает из него свои части — подмена «на лету» их бы сломала.
            _staged = staged;
            _log.LogWarning("Сервер обновлён до {Version} — перезапуск", package.Version);
            _ = Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ => Restart(program, staged));
            return Done(new UpdateResult(true, $"Установлена версия {package.Version}, сервер перезапускается", package.Version.ToString()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Не удалось обновить сервер");
            return Fail(ex is UnauthorizedAccessException && !OperatingSystem.IsWindows()
                ? $"Нет прав на папку {Path.GetDirectoryName(program)}. Один раз выполните на сервере «sudo ./officechat-server install» новой версией — дальше обновления пойдут сами"
                : "Не удалось сохранить новую версию: " + ex.Message);
        }
        finally
        {
            _busy.Release();
        }

        UpdateResult Fail(string message)
        {
            try { File.Delete(staged); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _log.LogWarning("Обновление сервера отклонено: {Reason}", message);
            return Done(UpdateResult.Fail(message));
        }
    }

    private UpdateResult Done(UpdateResult result)
    {
        LastCheck = $"{DateTime.Now:dd.MM.yyyy HH:mm} — {result.Message}";
        return result;
    }

    private void Restart(string program, string staged)
    {
        RestartRequested = true;
        try
        {
            if (!SystemdHelpers.IsSystemdService())
            {
                // Новая версия сама дождётся нашего выхода, встанет на место программы и запустит сервер:
                // служба Windows — снова службой, из консоли — с теми же параметрами.
                var service = OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService();
                Process.Start(new ProcessStartInfo(staged) { UseShellExecute = false, CreateNoWindow = service }
                    .WithArgs([ApplyCommand, program, service ? "service" : "console", WaitArgument(Environment.ProcessId),
                        .. Environment.GetCommandLineArgs().Skip(1).Where(a => !a.StartsWith(WaitPrefix, StringComparison.Ordinal))]));
            }
            // systemd: файл подменит Main после остановки (ApplyStaged), а запустит снова сам systemd — по коду выхода.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Не удалось подготовить перезапуск после обновления — запустите сервер вручную");
        }
        _lifetime.StopApplication();
    }

    /// <summary>Под systemd: сервер остановлен — поставить новую версию на место программы.</summary>
    public static void ApplyStaged()
    {
        if (_staged is not { } staged || ProgramPath is not { } program || !SystemdHelpers.IsSystemdService()) return;
        File.Move(staged, program, overwrite: true);
    }

    /// <summary>Остатки прошлого обновления.</summary>
    private static void CleanUp()
    {
        if (ProgramPath is not { } program || _staged != null) return;
        foreach (var leftover in new[] { program + ".old", program + ".new", program + ".tmp" })
        {
            try
            {
                if (File.Exists(leftover)) File.Delete(leftover);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // ---- Вспомогательный процесс и ожидание старой копии ----

    private const string WaitPrefix = "--wait-pid=";

    private static string WaitArgument(int pid) => WaitPrefix + pid;

    /// <summary>Убрать из аргументов «--wait-pid=N» и дождаться, пока процесс N завершится.</summary>
    public static string[] WaitForPrevious(string[] args)
    {
        foreach (var arg in args.Where(a => a.StartsWith(WaitPrefix, StringComparison.Ordinal)))
        {
            if (!int.TryParse(arg[WaitPrefix.Length..], out var pid)) continue;
            try
            {
                using var previous = Process.GetProcessById(pid);
                previous.WaitForExit(TimeSpan.FromMinutes(1));
            }
            catch (ArgumentException)
            {
                // Уже завершился.
            }
        }
        return args.Where(a => !a.StartsWith(WaitPrefix, StringComparison.Ordinal)).ToArray();
    }

    /// <summary>Команда новой версии: встать на место программы и запустить сервер.</summary>
    public const string ApplyCommand = "apply-update";

    /// <summary>
    /// Запущены из officechat-server.new, старая копия уже вышла (см. <see cref="WaitForPrevious"/>):
    /// apply-update ПРОГРАММА service|console [параметры запуска]. Копируем себя на место программы и запускаем её.
    /// </summary>
    public static int Apply(string[] args)
    {
        if (args.Length < 2 || Environment.ProcessPath is not { } self) return 2;
        var (program, mode, runArgs) = (args[0], args[1], args[2..]);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (File.Exists(program)) File.Move(program, program + ".old", overwrite: true);
                    File.Copy(self, program, overwrite: true);
                }
                else
                {
                    File.Copy(self, program + ".tmp", overwrite: true);
                    File.Move(program + ".tmp", program, overwrite: true);
                }
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 20)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1)); // старая копия ещё держит файл
            }
        }

        if (mode == "service")
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                using var sc = Process.Start(new ProcessStartInfo("sc.exe") { UseShellExecute = false, CreateNoWindow = true }
                    .WithArgs("start", ServerApp.ServiceName));
                sc?.WaitForExit();
                if (sc?.ExitCode == 0) return 0;
                Thread.Sleep(TimeSpan.FromSeconds(3));
            }
            return 1;
        }
        Process.Start(new ProcessStartInfo(program) { UseShellExecute = OperatingSystem.IsWindows() }.WithArgs(runArgs));
        return 0;
    }
}

/// <summary>Итог обновления. Version — какая версия установлена (null — ничего не поменялось).</summary>
public sealed record UpdateResult(bool Ok, string Message, string? Version)
{
    public static UpdateResult Fail(string message) => new(false, message, null);
}

internal static class ProcessStartInfoArgs
{
    public static ProcessStartInfo WithArgs(this ProcessStartInfo start, params IEnumerable<string> args)
    {
        foreach (var arg in args) start.ArgumentList.Add(arg);
        return start;
    }
}
