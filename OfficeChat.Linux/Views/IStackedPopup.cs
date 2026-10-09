using OfficeChat.Models;

namespace OfficeChat.Views;

/// <summary>Окно в стопке справа внизу (уведомление о сообщении или приглашение в игру).</summary>
public interface IStackedPopup
{
    Contact Contact { get; }

    bool IsClosing { get; }

    void FadeOutAndClose();
}
