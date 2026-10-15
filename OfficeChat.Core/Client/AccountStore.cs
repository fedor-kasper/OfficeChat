using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OfficeChat.Services;

namespace OfficeChat.Client;

/// <summary>Аккаунт, в который вошли на этом компьютере (под этой учётной записью Windows/Linux).</summary>
public sealed class SavedAccount
{
    public Guid Id { get; set; }
    public string Login { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ServerHost { get; set; } = "";
    public int ServerPort { get; set; }
    public string ServerName { get; set; } = "";
    /// <summary>Отпечаток сертификата сервера — другому серверу ключ не отдаём.</summary>
    public string ServerFingerprint { get; set; } = "";
    /// <summary>Ключ устройства, зашифрованный (Windows — DPAPI; Linux — файл доступен только владельцу).</summary>
    public string ProtectedToken { get; set; } = "";
    public bool IsAdmin { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// Аккаунты этого компьютера: accounts.json в общей папке программы. У каждого аккаунта — своя папка данных
/// (accounts\Id): история, файлы, настройки, — так на одном компьютере могут работать несколько человек.
/// </summary>
public static class AccountStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OfficeChat device token");

    private static string FilePath => Path.Combine(SettingsService.RootFolder, "accounts.json");

    private sealed class Data
    {
        public Guid? LastUsed { get; set; }
        public List<SavedAccount> Accounts { get; set; } = new();
    }

    private static Data Read()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn("Не удалось прочитать список аккаунтов", ex);
        }
        return new Data();
    }

    private static void Write(Data data)
    {
        Directory.CreateDirectory(SettingsService.RootFolder);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, FilePath, overwrite: true);
    }

    public static List<SavedAccount> All() => Read().Accounts;

    public static SavedAccount? Find(Guid id) => Read().Accounts.FirstOrDefault(a => a.Id == id);

    /// <summary>С каким аккаунтом работали последним (его открываем по умолчанию).</summary>
    public static SavedAccount? LastUsed()
    {
        var data = Read();
        return data.Accounts.FirstOrDefault(a => a.Id == data.LastUsed) ?? data.Accounts.FirstOrDefault();
    }

    public static void Save(SavedAccount account, bool makeLastUsed = true)
    {
        var data = Read();
        data.Accounts.RemoveAll(a => a.Id == account.Id);
        data.Accounts.Add(account);
        if (makeLastUsed) data.LastUsed = account.Id;
        Write(data);
    }

    public static void MarkUsed(Guid id)
    {
        var data = Read();
        if (data.LastUsed == id) return;
        data.LastUsed = id;
        Write(data);
    }

    /// <summary>Выход: аккаунт убирается из списка, его данные на диске остаются (при новом входе — пригодятся).</summary>
    public static void Remove(Guid id)
    {
        var data = Read();
        data.Accounts.RemoveAll(a => a.Id == id);
        if (data.LastUsed == id) data.LastUsed = data.Accounts.FirstOrDefault()?.Id;
        Write(data);
    }

    public static string FolderOf(Guid id) => Path.Combine(SettingsService.RootFolder, "accounts", id.ToString("N"));

    public static string Protect(string token)
    {
        if (OperatingSystem.IsWindows())
            return "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy,
                DataProtectionScope.CurrentUser));
        return "plain:" + token;
    }

    public static string? Unprotect(string stored)
    {
        try
        {
            if (stored.StartsWith("dpapi:", StringComparison.Ordinal) && OperatingSystem.IsWindows())
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[6..]), Entropy,
                    DataProtectionScope.CurrentUser));
            if (stored.StartsWith("plain:", StringComparison.Ordinal)) return stored[6..];
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Log.Warn("Не удалось расшифровать ключ входа — нужно войти заново", ex);
        }
        return null;
    }
}
