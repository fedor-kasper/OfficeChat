using System.Windows;
using System.Windows.Media.Animation;
using OfficeChat.Models;

namespace OfficeChat.Views;

/// <summary>Всплывающее приглашение в игру. Висит, пока не ответят (или пригласивший не передумает).</summary>
public partial class GameInviteWindow : Window, IStackedPopup
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(200));

    public BoardGame Game { get; }

    public Contact Contact => Game.Opponent;

    public bool IsClosing { get; private set; }

    public event Action<GameInviteWindow>? Accepted;
    public event Action<GameInviteWindow>? Declined;

    public GameInviteWindow(BoardGame game)
    {
        InitializeComponent();
        Game = game;
        SenderText.Text = game.Opponent.Title;
        InviteText.Text = $"Предлагает сыграть: {game.Title.ToLowerInvariant()}. {game.InviteHint}";

        // Пригласивший отменил приглашение — убираем окно.
        game.Changed += OnGameChanged;
        Closed += (_, _) => game.Changed -= OnGameChanged;

        Loaded += (_, _) => BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeDuration));
    }

    private void OnGameChanged()
    {
        if (Game.State != GameState.Invited)
            FadeOutAndClose();
    }

    public void FadeOutAndClose()
    {
        if (IsClosing) return;
        IsClosing = true;

        var fade = new DoubleAnimation(0, FadeDuration);
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => Accepted?.Invoke(this);

    private void Decline_Click(object sender, RoutedEventArgs e) => Declined?.Invoke(this);
}
