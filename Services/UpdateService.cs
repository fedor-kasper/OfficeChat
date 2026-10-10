using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using OfficeChat.Models;

namespace OfficeChat.Services;

/// <summary>Подписанная сборка программы: какая версия и для какой системы.</summary>
public sealed record SignedPackage(Version Version, string Platform);

/// <summary>
/// Обновление по локальной сети без сервера. Каждая копия сообщает в пакетах обнаружения свою версию.
/// Увидев у коллеги более новую сборку для своей системы, программа просит её прислать, проверяет
/// цифровую подпись (подписывает только сборка в GitHub Actions — подделать обновление не выйдет),
/// а когда окно свёрнуто в трей и нет игр и передач, подменяет свой файл и перезапускается.
///
/// Подпись лежит в конце самого файла программы (exe или AppImage): так её можно передать
/// дальше вместе с файлом, и коллега обновится уже от нас.
/// </summary>
public sealed class UpdateService : IDisposable
{
    /// <summary>Открытый ключ подписи сборок (ECDSA P-256). Закрытый — только в секретах GitHub.</summary>
    private const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEM5qECcKwJ0xWUuoayW45xaPQ1TE7LtFqfLr6BwKHn5lFNHzpwPpoVaq7nCriwvd/tc41G220DFrgYwoEs5MxNw==";

    /// <summary>Метка в последних 8 байтах подписанного файла.</summary>
    private static readonly byte[] Magic = "OCSIGv01"u8.ToArray();

    /// <summary>Аргумент перезапуска: дождаться выхода старой копии (по номеру процесса).</summary>
    public const string WaitArgument = "--wait-pid=";

    /// <summary>Аргумент перезапуска: программа только что обновилась.</summary>
    public const string UpdatedArgument = "--updated";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);
    // Не досаждаем одному и тому же компьютеру просьбами: повтор — не раньше чем через 10 минут.
    private static readonly TimeSpan RequestCooldown = TimeSpan.FromMinutes(10);

    private readonly ChatService _chat;
    private readonly Func<bool> _canRestartNow;
    private readonly Action _exit;
    private readonly DispatcherTimer _timer;
    private readonly Dictionary<Guid, DateTime> _requested = new();
    private SignedPackage? _staged;
    private bool _installing;
    private bool _deferLogged;

    /// <summary>Наша система: обновляться можно только сборкой для неё же.</summary>
    public static string Platform => OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";

    /// <summary>Версия работающей программы.</summary>
    public Version CurrentVersion { get; private set; }

    /// <summary>Наш файл подписан — его можно раздавать коллегам.</summary>
    public bool CanShare { get; private set; }

    /// <summary>Где лежит файл программы (exe или AppImage); null — запущено не из готовой сборки.</summary>
    public static string? PackagePath =>
        OperatingSystem.IsWindows() ? Environment.ProcessPath : Environment.GetEnvironmentVariable("APPIMAGE");

    private static string StagingFolder => Path.Combine(SettingsService.DataFolder, "update");

    private static string StagedPath =>
        Path.Combine(StagingFolder, OperatingSystem.IsWindows() ? "OfficeChat.exe" : "OfficeChat.AppImage");

    /// <param name="canRestartNow">Можно ли перезапуститься прямо сейчас (окно в трее, нет игр и передач).</param>
    /// <param name="exit">Закрыть программу (новая копия уже запущена и ждёт).</param>
    public UpdateService(ChatService chat, Func<bool> canRestartNow, Action exit)
    {
        _chat = chat;
        _canRestartNow = canRestartNow;
        _exit = exit;
        CurrentVersion = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0);
        _chat.UpdatePacketReceived += OnPacket;

        _timer = new DispatcherTimer { Interval = CheckInterval };
        _timer.Tick += (_, _) => Tick();
        _ = InitializeAsync();
    }

    /// <summary>Проверяем подпись своего файла и уже скачанного обновления (в фоне: файл большой).</summary>
    private async Task InitializeAsync()
    {
        var own = PackagePath is { } path ? await Task.Run(() => Verify(path)) : null;
        if (own != null && own.Platform == Platform)
        {
            CurrentVersion = own.Version;
            CanShare = true;
        }
        _chat.SetUpdateInfo(CurrentVersion.ToString(), Platform, CanShare);
        Log.Info($"Версия {CurrentVersion} ({Platform}), подписанная сборка: {(CanShare ? "да" : "нет")}");

        if (File.Exists(StagedPath))
        {
            var staged = await Task.Run(() => Verify(StagedPath));
            if (staged != null && staged.Platform == Platform && staged.Version > CurrentVersion)
                _staged = staged;
            else
                FileStore.DeleteQuietly(StagedPath);
        }
        _timer.Start();
        Tick();
    }

    private void Tick()
    {
        if (_staged != null)
        {
            if (_canRestartNow())
                Install();
            else if (!_deferLogged)
            {
                _deferLogged = true;
                Log.Info($"Обновление {_staged.Version} ждёт: окно открыто, идёт игра или передаётся файл");
            }
            return;
        }
        RequestFromBestPeer();
    }

    // ---- Скачивание ----

    private void RequestFromBestPeer()
    {
        // Самая новая подписанная сборка для нашей системы у тех, кто в сети.
        var candidate = _chat.People
            .Select(c => c.Peer!)
            .Where(p => p.IsOnline && p.CanShareUpdate && p.Platform == Platform)
            .Select(p => (Peer: p, Version: Version.TryParse(p.AppVersion, out var v) ? v : null))
            .Where(x => x.Version != null && x.Version > CurrentVersion)
            .OrderByDescending(x => x.Version)
            .FirstOrDefault(x => !_requested.TryGetValue(x.Peer.Id, out var at) || DateTime.UtcNow - at > RequestCooldown);
        if (candidate.Peer == null) return;

        _requested[candidate.Peer.Id] = DateTime.UtcNow;
        Log.Info($"У «{candidate.Peer.Name}» версия {candidate.Version} — просим прислать обновление");
        if (_chat.FindContact(candidate.Peer.Id) is { } contact)
            _ = _chat.SendPacketAsync(contact, new ChatPacket { Type = ChatPacket.UpdateRequest, Id = Guid.NewGuid() });
    }

    private void OnPacket(Contact contact, ChatPacket packet)
    {
        switch (packet.Type)
        {
            case ChatPacket.UpdateRequest:
                _ = SendOwnPackageAsync(contact);
                break;
            case ChatPacket.UpdatePackage when packet.ReceivedPayloadPath is { } file:
                _ = AcceptPackageAsync(contact, file);
                break;
        }
    }

    /// <summary>Коллега просит наш файл программы — отправляем, если он подписан.</summary>
    private async Task SendOwnPackageAsync(Contact contact)
    {
        if (!CanShare || PackagePath is not { } path) return;
        Log.Info($"«{contact.Title}» просит обновление — отправляем версию {CurrentVersion}");
        var sent = await _chat.SendPacketAsync(contact, new ChatPacket
        {
            Type = ChatPacket.UpdatePackage,
            Id = Guid.NewGuid(),
            Text = CurrentVersion.ToString(),
            FileName = Platform,
            PayloadPath = path,
        });
        if (!sent) Log.Warn($"Не удалось отправить обновление «{contact.Title}»");
    }

    /// <summary>Пришло обновление: принимаем, только если просили, подпись верна и версия новее.</summary>
    private async Task AcceptPackageAsync(Contact contact, string file)
    {
        try
        {
            if (!_requested.ContainsKey(contact.Peer!.Id) || _staged != null)
                return;
            var package = await Task.Run(() => Verify(file));
            if (package == null || package.Platform != Platform)
            {
                Log.Warn($"Обновление от «{contact.Title}» отклонено: подпись не сошлась");
                return;
            }
            if (package.Version <= CurrentVersion) return;

            Directory.CreateDirectory(StagingFolder);
            File.Move(file, StagedPath, overwrite: true);
            _staged = package;
            Log.Info($"Получено обновление {package.Version} от «{contact.Title}» — установим, когда окно будет свёрнуто");
            Tick();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("Не удалось сохранить обновление", ex);
        }
        finally
        {
            FileStore.DeleteQuietly(file);
        }
    }

    // ---- Установка ----

    /// <summary>Подменяем файл программы новым и перезапускаемся (в трей).</summary>
    private void Install()
    {
        if (_installing || _staged is not { } staged || PackagePath is not { } target) return;
        _installing = true;
        var backup = target + ".old";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Работающий exe нельзя перезаписать, но можно переименовать — освобождаем место для нового.
                FileStore.DeleteQuietly(backup);
                File.Move(target, backup);
                try
                {
                    File.Copy(StagedPath, target);
                }
                catch
                {
                    File.Move(backup, target);
                    throw;
                }
            }
            else
            {
                // В Linux файл можно заменить и на ходу: работающая копия держит старый.
                var temp = target + ".new";
                File.Copy(StagedPath, temp, overwrite: true);
                File.SetUnixFileMode(temp, File.GetUnixFileMode(target) | UnixFileMode.UserExecute);
                File.Move(temp, target, overwrite: true);
            }
            FileStore.DeleteQuietly(StagedPath);

            Log.Info($"Установлено обновление {staged.Version} — перезапускаемся");
            Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = false,
                ArgumentList =
                {
                    AutoStartArgument,
                    WaitArgument + Environment.ProcessId,
                    UpdatedArgument,
                },
            });
            _exit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Чаще всего — нет прав на папку программы (например, Program Files). Пробуем снова после перезапуска.
            Log.Error($"Не удалось установить обновление {staged.Version} в {target}", ex);
            _staged = null;
            _timer.Stop();
        }
        finally
        {
            _installing = false;
        }
    }

    /// <summary>Аргумент запуска «сразу в трей» (как при автозапуске).</summary>
    private const string AutoStartArgument = "--tray";

    /// <summary>
    /// Запуск после обновления: дождаться выхода старой копии (иначе она ещё держит «единственный экземпляр»)
    /// и убрать оставшийся от неё файл.
    /// </summary>
    public static void FinishRestart(string[] args)
    {
        var wait = args.FirstOrDefault(a => a.StartsWith(WaitArgument, StringComparison.Ordinal));
        if (wait != null && int.TryParse(wait[WaitArgument.Length..], out var pid))
        {
            try
            {
                using var old = Process.GetProcessById(pid);
                old.WaitForExit(TimeSpan.FromSeconds(30));
            }
            catch (ArgumentException)
            {
                // Старая копия уже завершилась.
            }
        }
        if (PackagePath is { } path)
            FileStore.DeleteQuietly(path + ".old");
    }

    // ---- Подпись ----

    /// <summary>
    /// Проверяет подпись в конце файла. Формат: [программа][JSON с версией, системой и подписью]
    /// [длина JSON, 8 байт][метка «OCSIGv01»]. Подписаны система, версия и SHA-256 программы.
    /// null — подписи нет или она неверна.
    /// </summary>
    public static SignedPackage? Verify(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length < 16) return null;

            var tail = new byte[16];
            file.Seek(-16, SeekOrigin.End);
            file.ReadExactly(tail);
            if (!tail.AsSpan(8).SequenceEqual(Magic)) return null;
            var blockLength = BinaryPrimitives.ReadInt64LittleEndian(tail);
            if (blockLength is <= 0 or > 4096) return null;
            var contentLength = file.Length - 16 - blockLength;
            if (contentLength <= 0) return null;

            var block = new byte[blockLength];
            file.Seek(contentLength, SeekOrigin.Begin);
            file.ReadExactly(block);
            var info = JsonSerializer.Deserialize<SignatureBlock>(block);
            if (info == null || !Version.TryParse(info.Version, out var version)) return null;

            file.Seek(0, SeekOrigin.Begin);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            var left = contentLength;
            while (left > 0)
            {
                var read = file.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (read == 0) return null;
                sha.AppendData(buffer, 0, read);
                left -= read;
            }
            var hash = Convert.ToHexStringLower(sha.GetHashAndReset());

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
            var signed = Encoding.UTF8.GetBytes($"{info.Platform}\n{info.Version}\n{hash}");
            var ok = ecdsa.VerifyData(signed, Convert.FromBase64String(info.Signature), HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
            return ok ? new SignedPackage(version, info.Platform) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                       or FormatException or CryptographicException)
        {
            return null;
        }
    }

    private sealed class SignatureBlock
    {
        [System.Text.Json.Serialization.JsonPropertyName("version")]
        public string Version { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("platform")]
        public string Platform { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("signature")]
        public string Signature { get; set; } = "";
    }

    public void Dispose()
    {
        _timer.Stop();
        _chat.UpdatePacketReceived -= OnPacket;
    }
}
