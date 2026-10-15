using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OfficeChat.Client;
using OfficeChat.Mobile.Views;
using OfficeChat.Services;

namespace OfficeChat.Mobile;

/// <summary>Весь экран приложения: стопка страниц, сверху — текущая. «Назад» снимает верхнюю.</summary>
public sealed class ShellView : UserControl
{
    private readonly Panel _host = new();
    private readonly List<Page> _stack = new();

    /// <summary>Экран приложения (на телефоне он один).</summary>
    public static ShellView? Instance { get; private set; }

    public ShellView()
    {
        Instance = this;
        Content = _host;
        Background = Avalonia.Media.Brushes.White;
        AttachedToVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is { } top) top.BackRequested += OnBackRequested;
            if (_stack.Count == 0) ShowStart();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is { } top) top.BackRequested -= OnBackRequested;
            MobileSession.SessionExpired -= OnSessionExpired;
        };
        MobileSession.SessionExpired += OnSessionExpired;
    }

    public Page? Current => _stack.Count > 0 ? _stack[^1] : null;

    /// <summary>Первая страница: переписки, если вход уже выполнен; иначе вход.</summary>
    private void ShowStart()
    {
        if (MobileSession.IsRunning)
        {
            SetRoot(new ChatsPage());
            return;
        }
        var account = MobileSession.SavedAccount();
        if (account != null && MobileSession.Start(account))
            SetRoot(new ChatsPage());
        else
            SetRoot(new LoginPage(account));
    }

    /// <summary>Начать заново с этой страницы (после входа, выхода).</summary>
    public void SetRoot(Page page)
    {
        foreach (var old in _stack) old.OnDisappearing();
        _stack.Clear();
        _host.Children.Clear();
        Push(page);
    }

    public void Push(Page page)
    {
        Current?.OnDisappearing();
        if (Current != null) Current.IsVisible = false;
        page.Shell = this;
        _stack.Add(page);
        _host.Children.Add(page);
        page.OnAppearing();
    }

    public void Pop()
    {
        if (_stack.Count <= 1) return;
        var top = _stack[^1];
        top.OnDisappearing();
        _stack.RemoveAt(_stack.Count - 1);
        _host.Children.Remove(top);
        var current = _stack[^1];
        current.IsVisible = true;
        current.OnAppearing();
    }

    // ---- Окна поверх страницы (вопросы, ввод текста) ----

    private readonly List<Control> _overlays = new();

    /// <summary>Показать поверх страницы; «Назад» закрывает верхнее (вызывая onBack).</summary>
    public void ShowOverlay(Control overlay, Action onBack)
    {
        overlay.Tag = onBack;
        _overlays.Add(overlay);
        _host.Children.Add(overlay);
    }

    public void CloseOverlay(Control overlay)
    {
        _overlays.Remove(overlay);
        _host.Children.Remove(overlay);
    }

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (_overlays.Count > 0)
        {
            (_overlays[^1].Tag as Action)?.Invoke();
            e.Handled = true;
            return;
        }
        if (Current?.HandleBack() == true)
        {
            e.Handled = true;
            return;
        }
        if (_stack.Count <= 1) return;
        Pop();
        e.Handled = true;
    }

    private void OnSessionExpired(string reason) => Dispatcher.UIThread.Post(() =>
    {
        var account = MobileSession.Account;
        MobileSession.Stop();
        Log.Info($"Телефон: {reason} — входим заново");
        SetRoot(new LoginPage(account, reason));
    });
}
