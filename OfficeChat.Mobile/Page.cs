using Avalonia.Controls;

namespace OfficeChat.Mobile;

/// <summary>Страница приложения: показывается на весь экран, «Назад» возвращает на предыдущую.</summary>
public abstract class Page : UserControl
{
    /// <summary>Оболочка, в которой открыта страница.</summary>
    public ShellView Shell { get; internal set; } = null!;

    /// <summary>Страница снова на экране (открыли или вернулись на неё).</summary>
    public virtual void OnAppearing() { }

    /// <summary>Страницу закрыли или перекрыли другой.</summary>
    public virtual void OnDisappearing() { }

    /// <summary>«Назад» обработан самой страницей (например, закрыто меню) — true.</summary>
    public virtual bool HandleBack() => false;
}
