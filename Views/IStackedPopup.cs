namespace OfficeChat.Views;

/// <summary>Окно в стопке справа внизу (уведомление о сообщении, приглашение в игру, напоминание).</summary>
public interface IStackedPopup
{
    bool IsClosing { get; }

    void FadeOutAndClose();
}
