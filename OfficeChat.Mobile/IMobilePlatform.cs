using OfficeChat.Models;

namespace OfficeChat.Mobile;

/// <summary>То, что телефон делает по-своему: уведомления и работа в фоне.</summary>
public interface IMobilePlatform
{
    /// <summary>Держать программу в фоне (Android — служба с постоянным значком), чтобы сообщения приходили сразу.</summary>
    void StartBackground();

    void StopBackground();

    /// <summary>Уведомление о новом сообщении (по одному на переписку — новое заменяет старое).</summary>
    void NotifyMessage(Contact conversation, ChatMessage message, int unread);

    /// <summary>Убрать уведомления переписки (её открыли).</summary>
    void ClearMessageNotification(Guid conversationKey);

    /// <summary>Сработало напоминание.</summary>
    void NotifyReminder(Guid id, string title, string text, bool urgent);

    void ClearReminderNotification(Guid id);

    /// <summary>Приглашение в игру.</summary>
    void NotifyGame(Contact opponent, string text);

    /// <summary>Открыть файл из переписки в подходящей программе.</summary>
    Task OpenFileAsync(Avalonia.Controls.TopLevel top, string path);

    /// <summary>Описание устройства для сервера (видно в списке устройств).</summary>
    string DeviceName { get; }

    string PlatformName { get; }
}

/// <summary>Без платформы (окно на компьютере): уведомления — только в журнал.</summary>
public sealed class DesktopPlatform : IMobilePlatform
{
    public void StartBackground() { }
    public void StopBackground() { }
    public void NotifyMessage(Contact conversation, ChatMessage message, int unread) =>
        Services.Log.Info($"[уведомление] {conversation.Title}: {message.Text}");
    public void ClearMessageNotification(Guid conversationKey) { }
    public void NotifyReminder(Guid id, string title, string text, bool urgent) =>
        Services.Log.Info($"[напоминание] {title}: {text}");
    public void ClearReminderNotification(Guid id) { }
    public void NotifyGame(Contact opponent, string text) => Services.Log.Info($"[игра] {opponent.Title}: {text}");
    public async Task OpenFileAsync(Avalonia.Controls.TopLevel top, string path)
    {
        if (await top.StorageProvider.TryGetFileFromPathAsync(new Uri(path)) is { } file)
            await top.Launcher.LaunchFileAsync(file);
    }

    public string DeviceName => Environment.MachineName + " (проверка телефона)";
    public string PlatformName => "android-preview";
}
