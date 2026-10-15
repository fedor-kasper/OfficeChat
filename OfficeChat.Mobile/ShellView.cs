using Avalonia.Controls;
using Avalonia.Controls.Platform;
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
            if (TopLevel.GetTopLevel(this) is { } top)
            {
                top.BackRequested += OnBackRequested;
                if (top.InputPane is { } pane) pane.StateChanged += OnInputPaneChanged;
                if (top.InsetsManager is { } insets) insets.SafeAreaChanged += OnSafeAreaChanged;
            }
            UpdateInsets();
            if (_stack.Count == 0) ShowStart();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (TopLevel.GetTopLevel(this) is { } top)
            {
                top.BackRequested -= OnBackRequested;
                if (top.InputPane is { } pane) pane.StateChanged -= OnInputPaneChanged;
                if (top.InsetsManager is { } insets) insets.SafeAreaChanged -= OnSafeAreaChanged;
            }
            MobileSession.SessionExpired -= OnSessionExpired;
        };
        SizeChanged += (_, _) => UpdateInsets();
        MobileSession.SessionExpired += OnSessionExpired;
    }

    public Page? Current => _stack.Count > 0 ? _stack[^1] : null;

    /// <summary>Клавиатура открылась или закрылась (страница может прокрутить к полю ввода).</summary>
    public event Action<bool>? KeyboardChanged;

    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs e)
    {
        UpdateInsets();
        KeyboardChanged?.Invoke(e.NewState == InputPaneState.Open);
    }

    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e) => UpdateInsets();

    /// <summary>
    /// Телефон рисует программу на весь экран: сверху строка состояния, снизу кнопки и клавиатура. Отступаем от них,
    /// чтобы заголовок и поле ввода не оказались под ними (поле ввода поднимается вместе с клавиатурой).
    /// </summary>
    private void UpdateInsets()
    {
        var top = TopLevel.GetTopLevel(this);
        var safe = top?.InsetsManager?.SafeAreaPadding ?? default;
        double keyboard = 0;
        if (top?.InputPane is { State: InputPaneState.Open } pane && pane.OccludedRect.Height > 0)
            keyboard = Math.Max(0, Bounds.Height - pane.OccludedRect.Top);
        _host.Margin = new Avalonia.Thickness(safe.Left, safe.Top, safe.Right, Math.Max(safe.Bottom, keyboard));
    }

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

    /// <summary>Открыть переписку (нажали на уведомление).</summary>
    public void OpenConversation(Guid key)
    {
        if (MobileSession.Chat?.Contacts.FirstOrDefault(c => c.Key == key) is not { } contact) return;
        if (Current is ConversationPage page && page.Contact == contact) return;
        while (_stack.Count > 1) Pop();
        Push(new ConversationPage(contact));
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
