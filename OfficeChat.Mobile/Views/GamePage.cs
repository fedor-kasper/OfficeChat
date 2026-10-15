using Avalonia.Controls;
using Avalonia.Layout;
using OfficeChat.Models;
using OfficeChat.Views;

namespace OfficeChat.Mobile.Views;

/// <summary>Партия с коллегой — на весь экран (поле и кнопки — общие с Linux-версией).</summary>
public sealed class GamePage : Page
{
    private readonly BoardGame _game;
    private readonly GamePanel _panel = new();

    public GamePage(BoardGame game)
    {
        _game = game;
        var back = new Button { Content = "←", Classes = { "header" } };
        back.Click += (_, _) => Shell.Pop();
        var header = new Border
        {
            Background = (Avalonia.Media.IBrush)Avalonia.Application.Current!.FindResource("HeaderBrush")!,
            Padding = new Avalonia.Thickness(2, 8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    back,
                    new TextBlock
                    {
                        Text = $"{game.Title} · {game.Opponent.Title}", FontSize = 17, FontWeight = Avalonia.Media.FontWeight.SemiBold,
                        Foreground = Avalonia.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(new Viewbox { Child = _panel, Stretch = Avalonia.Media.Stretch.Uniform, StretchDirection = Avalonia.Media.StretchDirection.Both,
            VerticalAlignment = VerticalAlignment.Top, MaxWidth = 560 });
        Content = dock;
        if (MobileSession.Games is { } games) _panel.Attach(games);
    }

    public override void OnAppearing()
    {
        _panel.Show(_game);
        _game.Changed += OnChanged;
    }

    public override void OnDisappearing() => _game.Changed -= OnChanged;

    // Партию закрыли («Закрыть» после окончания) — возвращаемся в переписку.
    private void OnChanged()
    {
        if (MobileSession.Games?.GameFor(_game.Opponent) != _game && Shell.Current == this) Shell.Pop();
    }
}
