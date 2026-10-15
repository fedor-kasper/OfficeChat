using System.IO;
using OfficeChat.Services;

namespace OfficeChat.Client;

/// <summary>Переписка, которая велась на этом компьютере без аккаунта.</summary>
/// <param name="UserId">Старый Id программы на этом компьютере.</param>
/// <param name="DisplayName">Под каким именем её видели коллеги.</param>
/// <param name="MessageCount">Сколько в ней сообщений.</param>
public sealed record LegacyHistory(Guid UserId, string DisplayName, int MessageCount);

/// <summary>
/// Перенос переписки «без аккаунта» в аккаунт при первом входе: старый Id компьютера закрепляется за аккаунтом
/// на сервере (коллеги увидят, что это тот же человек), история копируется в папку аккаунта и потом
/// уходит на сервер — на все устройства. Сама старая переписка не удаляется.
/// </summary>
public static class LegacyMigration
{
    // Изображения и стикеры ищутся в папке аккаунта — их копируем (они небольшие). Файлы в истории записаны
    // полными путями — остаются на месте, без копии (их бывает много).
    private static readonly string[] Folders = { "images", "stickers" };

    private static string LegacyHistoryPath => Path.Combine(SettingsService.RootFolder, "history.db");

    /// <summary>
    /// Что предложить перенести в этот аккаунт. null — нечего: старой переписки нет, она уже перенесена
    /// (в этот или другой аккаунт) или переносить уже предлагали.
    /// </summary>
    public static LegacyHistory? Offer(SavedAccount account)
    {
        try
        {
            if (!File.Exists(LegacyHistoryPath)) return null;
            if (SettingsService.LoadFrom(AccountStore.FolderOf(account.Id)).LegacyOffered) return null;
            var legacy = SettingsService.LoadFrom(SettingsService.RootFolder);
            if (legacy.MigratedTo != null) return null;
            int count;
            using (var store = new HistoryStore(LegacyHistoryPath))
                count = store.CountMessages();
            return count == 0 ? null : new LegacyHistory(legacy.UserId, legacy.DisplayName, count);
        }
        catch (Exception ex)
        {
            Log.Warn("Не удалось проверить переписку без аккаунта", ex);
            return null;
        }
    }

    /// <summary>Вопрос человеку: переносить ли.</summary>
    public static string Question(SavedAccount account, LegacyHistory legacy) =>
        $"На этом компьютере есть переписка без аккаунта: {legacy.MessageCount} сообщ." +
        (legacy.DisplayName.Length > 0 ? $", вас видели как «{legacy.DisplayName}»" : "") + ".\n\n" +
        $"Перенести её в аккаунт «{account.Login}»? Она появится на всех ваших устройствах, " +
        "а коллеги увидят, что это вы.\n\n" +
        "Если это переписка другого человека (общий компьютер) — нажмите «Нет».";

    /// <summary>Больше не предлагать (ответили «нет»: например, это переписка другого человека за общим компьютером).</summary>
    public static void Decline(SavedAccount account)
    {
        var folder = AccountStore.FolderOf(account.Id);
        var settings = SettingsService.LoadFrom(folder);
        settings.LegacyOffered = true;
        SettingsService.SaveTo(folder, settings);
        Log.Info($"Перенос переписки без аккаунта в «{account.Login}» отклонён");
    }

    /// <summary>
    /// Перенести: закрепить старый Id за аккаунтом, скопировать историю, изображения и настройки в папку аккаунта.
    /// На сервер история уйдёт после запуска (<see cref="ChatService"/>), с докачкой после обрыва.
    /// Исключение — не получилось (сообщение для человека — в тексте).
    /// </summary>
    public static async Task MigrateAsync(SavedAccount account, ServerSession session, LegacyHistory legacy)
    {
        var claim = await session.Api.ClaimLegacyAsync(new List<Guid> { legacy.UserId });
        if (claim.Taken.Contains(legacy.UserId))
            throw new InvalidOperationException("Эта переписка уже перенесена в другой аккаунт.");

        // Кто из коллег уже перешёл на аккаунты — их старые Id сразу заменяем.
        List<Protocol.LegacyMapping> mappings;
        try
        {
            mappings = await session.Api.LegacyMappingsAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or ServerApiException or TaskCanceledException)
        {
            mappings = new();
        }

        var root = SettingsService.RootFolder;
        var folder = AccountStore.FolderOf(account.Id);
        Directory.CreateDirectory(folder);
        foreach (var name in Folders)
            CopyFolder(Path.Combine(root, name), Path.Combine(folder, name));

        // Старую базу открываем через HistoryStore: так у неё появятся все нужные колонки.
        new HistoryStore(LegacyHistoryPath).Dispose();
        using (var store = new HistoryStore(Path.Combine(folder, "history.db")))
        {
            store.CopyHistoryFrom(LegacyHistoryPath);
            store.RemapPeer(legacy.UserId, account.Id);
            foreach (var mapping in mappings.Where(m => m.LegacyId != legacy.UserId))
                store.RemapPeer(mapping.LegacyId, mapping.AccountId);
            store.SetState(ChatService.ImportStateKey, ChatService.ImportPending);
        }

        // Настройки: закрепы, «без уведомлений», стикеры, автозапуск.
        var old = SettingsService.LoadFrom(root);
        var settings = SettingsService.LoadFrom(folder);
        Guid Map(Guid id) => id == legacy.UserId ? account.Id
            : mappings.FirstOrDefault(m => m.LegacyId == id)?.AccountId ?? id;
        settings.AutoStart = old.AutoStart;
        settings.PinnedChats = settings.PinnedChats.Concat(old.PinnedChats.Select(Map)).Distinct().ToList();
        settings.MutedChats = settings.MutedChats.Concat(old.MutedChats.Select(Map)).Distinct().ToList();
        settings.RecentStickers = settings.RecentStickers.Concat(old.RecentStickers).Distinct().ToList();
        settings.LegacyOffered = true;
        SettingsService.SaveTo(folder, settings);

        old.MigratedTo = account.Id;
        SettingsService.SaveTo(root, old);
        Log.Info($"Переписка без аккаунта ({legacy.MessageCount} сообщений, Id {legacy.UserId}) перенесена в «{account.Login}»");
    }

    private static void CopyFolder(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            if (File.Exists(destination)) continue;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Не удалось перенести файл {file}", ex);
            }
        }
    }
}
