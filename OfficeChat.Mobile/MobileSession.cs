using OfficeChat.Client;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Mobile;

/// <summary>
/// Работа с аккаунтом на телефоне: служба переписки, напоминания и игры живут, пока жив процесс (на Android его
/// держит фоновая служба), а экраны приложения могут создаваться и закрываться сколько угодно раз.
/// </summary>
public static class MobileSession
{
    public static IMobilePlatform Platform { get; set; } = new DesktopPlatform();

    public static SavedAccount? Account { get; private set; }
    public static AppSettings? Settings { get; private set; }
    public static ChatService? Chat { get; private set; }
    public static ReminderService? Reminders { get; private set; }
    public static GameService? Games { get; private set; }

    public static bool IsRunning => Chat != null;

    /// <summary>Переписка, открытая на экране (её сообщения сразу прочитаны, уведомлений по ней нет).</summary>
    public static Contact? VisibleConversation { get; set; }

    /// <summary>Приложение на экране (а не в фоне).</summary>
    public static bool IsForeground { get; set; } = true;

    /// <summary>Сервер больше не принимает вход — нужно войти заново.</summary>
    public static event Action<string>? SessionExpired;

    /// <summary>Сеанс запущен или остановлен.</summary>
    public static event Action? Changed;

    /// <summary>Сохранённый аккаунт, с которым запускаться (на телефоне — один, последний).</summary>
    public static SavedAccount? SavedAccount() => AccountStore.LastUsed();

    /// <summary>Запуск с аккаунтом. false — ключ входа не прочитался, нужно войти заново.</summary>
    public static bool Start(SavedAccount account)
    {
        if (IsRunning) return true;
        var session = AccountLauncher.CreateSession(account);
        if (session == null) return false;

        Account = account;
        Settings = AccountLauncher.Activate(account);
        var chat = new ChatService(Settings)
        {
            IsConversationVisible = contact => IsForeground && contact == VisibleConversation,
        };
        chat.UseServer(session);
        chat.SessionExpired += reason => SessionExpired?.Invoke(reason);
        chat.MessageReceived += OnMessageReceived;
        Games = new GameService(chat);
        Games.InviteReceived += game => Platform.NotifyGame(game.Opponent, $"зовёт сыграть: {game.Title}");
        chat.Start();
        Chat = chat;
        Reminders = new ReminderService(chat);
        Reminders.ReminderDue += reminder =>
            Platform.NotifyReminder(reminder.Id, "Напоминание", reminder.Text, reminder.Importance >= 4);
        Reminders.ReminderRemoved += reminder => Platform.ClearReminderNotification(reminder.Id);
        Reminders.Start();
        Platform.StartBackground();
        Log.Info($"Телефон: сеанс «{account.Login}» запущен");
        Changed?.Invoke();
        return true;
    }

    private static void OnMessageReceived(Contact contact, ChatMessage message)
    {
        if (contact.IsMuted || (IsForeground && contact == VisibleConversation)) return;
        Platform.NotifyMessage(contact, message, contact.UnreadCount);
    }

    /// <summary>Переписку открыли — её уведомления больше не нужны.</summary>
    public static void Opened(Contact contact)
    {
        VisibleConversation = contact;
        Platform.ClearMessageNotification(contact.Key);
        Chat?.MarkRead(contact);
    }

    public static void Stop()
    {
        if (!IsRunning) return;
        Reminders?.Dispose();
        Games?.Dispose();
        Chat?.Dispose();
        Reminders = null;
        Games = null;
        Chat = null;
        Platform.StopBackground();
        Changed?.Invoke();
    }

    /// <summary>Выйти из аккаунта на этом телефоне.</summary>
    public static async Task SignOutAsync()
    {
        var account = Account;
        var session = Chat?.Session;
        Stop();
        if (account != null) await AccountLauncher.SignOutAsync(account, session);
        Account = null;
    }
}
