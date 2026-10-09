using Avalonia.Controls;
using Avalonia.Interactivity;
using OfficeChat.Models;

namespace OfficeChat.Views;

/// <summary>Всплывающее приглашение в крестики-нолики. Висит, пока не ответят (или пригласивший не передумает).</summary>
public partial class GameInviteWindow : Window, IStackedPopup
{
    public TicTacToeGame Game { get; }

    public Contact Contact => Game.Opponent;

    public bool IsClosing { get; private set; }

    public event Action<GameInviteWindow>? Accepted;
    public event Action<GameInviteWindow>? Declined;

    public GameInviteWindow() : this(null!) { }

    public GameInviteWindow(TicTacToeGame game)
    {
        InitializeComponent();
        Game = game;
        if (game == null) return; // конструктор для дизайнера

        SenderText.Text = game.Opponent.Title;
        game.Changed += OnGameChanged;
        Closed += (_, _) => game.Changed -= OnGameChanged;
        Popups.SetupFade(this);
    }

    private void OnGameChanged()
    {
        if (Game.State != GameState.Invited) FadeOutAndClose();
    }

    public void FadeOutAndClose()
    {
        if (IsClosing) return;
        IsClosing = true;
        Popups.FadeOutAndClose(this);
    }

    private void Accept_Click(object? sender, RoutedEventArgs e) => Accepted?.Invoke(this);

    private void Decline_Click(object? sender, RoutedEventArgs e) => Declined?.Invoke(this);
}
