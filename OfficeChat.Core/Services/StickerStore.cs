using System.IO;
using System.Reflection;

namespace OfficeChat.Services;

/// <summary>Стикер: где лежит картинка и как её прочитать.</summary>
public sealed record Sticker(string Id, string Name, Func<byte[]> Load);

/// <summary>Набор стикеров: встроенный в программу или свой (папка с картинками).</summary>
public sealed record StickerPack(string Id, string Title, bool IsBuiltIn, IReadOnlyList<Sticker> Stickers);

/// <summary>
/// Наборы стикеров. Встроенные зашиты в программу (Microsoft Fluent Emoji, лицензия MIT — Assets/Stickers),
/// свои — папки с картинками в «stickers» рядом с историей: их добавляют из любой папки, а полученный
/// стикер можно сохранить в «Мои стикеры». Отправляется стикер как изображение, поэтому получатель
/// видит его, даже если такого набора у него нет.
/// </summary>
public static class StickerStore
{
    public const string MyPackTitle = "Мои стикеры";
    private const string ResourcePrefix = "Stickers/";
    private const int MaxRecent = 24;
    // Стикер — не фотография: большие картинки в свой набор не берём.
    public const long MaxStickerBytes = 2 * 1024 * 1024;
    private const int MaxStickersPerPack = 300;

    private static readonly (string Folder, string Title)[] BuiltInPacks =
    {
        ("emotions", "Эмоции"),
        ("gestures", "Жесты"),
        ("office", "Офис"),
        ("animals", "Животные"),
        ("food", "Праздник и еда"),
    };

    public static string Folder => Path.Combine(SettingsService.DataFolder, "stickers");

    private static string MyPackFolder => Path.Combine(Folder, MyPackTitle);

    /// <summary>Все наборы: сначала встроенные, затем свои.</summary>
    public static List<StickerPack> LoadPacks()
    {
        var packs = new List<StickerPack>();
        var assembly = Assembly.GetExecutingAssembly();
        // Имена ресурсов: «Stickers/папка/01_имя.png» (при сборке на Windows разделитель может быть «\»).
        var resources = assembly.GetManifestResourceNames()
            .Select(n => (Resource: n, Path: n.Replace('\\', '/')))
            .Where(r => r.Path.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToList();
        foreach (var (folder, title) in BuiltInPacks)
        {
            var stickers = resources
                .Where(r => r.Path.StartsWith($"{ResourcePrefix}{folder}/", StringComparison.Ordinal))
                .OrderBy(r => r.Path, StringComparer.Ordinal)
                .Select(r => new Sticker("builtin/" + r.Path[ResourcePrefix.Length..], Path.GetFileNameWithoutExtension(r.Path),
                    () => ReadResource(assembly, r.Resource)))
                .ToList();
            if (stickers.Count > 0)
                packs.Add(new StickerPack("builtin/" + folder, title, true, stickers));
        }

        try
        {
            if (Directory.Exists(Folder))
            {
                foreach (var dir in Directory.GetDirectories(Folder)
                             .OrderBy(d => Path.GetFileName(d) == MyPackTitle ? 0 : 1)
                             .ThenBy(d => Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase))
                {
                    var stickers = Directory.GetFiles(dir)
                        .Where(ImageStore.IsImageFile)
                        .OrderBy(f => File.GetCreationTimeUtc(f))
                        .Select(f => new Sticker("user/" + Path.GetFileName(dir) + "/" + Path.GetFileName(f),
                            Path.GetFileNameWithoutExtension(f), () => File.ReadAllBytes(f)))
                        .ToList();
                    if (stickers.Count > 0)
                        packs.Add(new StickerPack("user/" + Path.GetFileName(dir), Path.GetFileName(dir), false, stickers));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Не удалось прочитать свои наборы стикеров", ex);
        }
        return packs;
    }

    private static byte[] ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>
    /// Свой набор из папки с картинками: копируем их к себе (папку потом можно удалить).
    /// Возвращает число добавленных стикеров.
    /// </summary>
    public static int AddPackFromFolder(string sourceFolder)
    {
        var images = Directory.GetFiles(sourceFolder)
            .Where(f => ImageStore.IsImageFile(f) && new FileInfo(f).Length <= MaxStickerBytes)
            .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxStickersPerPack)
            .ToList();
        if (images.Count == 0) return 0;

        var name = FileStore.SafeFileName(Path.GetFileName(sourceFolder.TrimEnd(Path.DirectorySeparatorChar, '/')));
        if (name.Length == 0) name = "Набор";
        var target = Path.Combine(Folder, name);
        for (var i = 2; Directory.Exists(target); i++)
            target = Path.Combine(Folder, $"{name} {i}");
        Directory.CreateDirectory(target);
        foreach (var image in images)
            File.Copy(image, Path.Combine(target, Path.GetFileName(image)));
        Log.Info($"Добавлен набор стикеров «{Path.GetFileName(target)}»: {images.Count} шт.");
        return images.Count;
    }

    /// <summary>Удалить свой набор (встроенные удалить нельзя).</summary>
    public static void DeletePack(StickerPack pack)
    {
        if (pack.IsBuiltIn) return;
        try
        {
            Directory.Delete(Path.Combine(Folder, pack.Title), recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Не удалось удалить набор стикеров «{pack.Title}»", ex);
        }
    }

    /// <summary>Сохранить картинку (например, полученный стикер) в «Мои стикеры».</summary>
    public static void SaveToMyStickers(string imagePath, string fileName)
    {
        Directory.CreateDirectory(MyPackFolder);
        var name = FileStore.SafeFileName(Path.GetFileNameWithoutExtension(fileName));
        if (name.Length == 0) name = "стикер";
        var extension = Path.GetExtension(imagePath);
        var target = Path.Combine(MyPackFolder, name + extension);
        for (var i = 2; File.Exists(target); i++)
            target = Path.Combine(MyPackFolder, $"{name} {i}{extension}");
        File.Copy(imagePath, target);
    }

    /// <summary>Недавние — для первой вкладки (последние сверху).</summary>
    public static List<Sticker> Recent(AppSettings settings, IEnumerable<StickerPack> packs)
    {
        var all = packs.SelectMany(p => p.Stickers).ToDictionary(s => s.Id);
        return settings.RecentStickers.Where(all.ContainsKey).Select(id => all[id]).ToList();
    }

    public static void MarkUsed(AppSettings settings, Sticker sticker)
    {
        settings.RecentStickers.Remove(sticker.Id);
        settings.RecentStickers.Insert(0, sticker.Id);
        if (settings.RecentStickers.Count > MaxRecent)
            settings.RecentStickers.RemoveRange(MaxRecent, settings.RecentStickers.Count - MaxRecent);
        SettingsService.Save(settings);
    }
}
