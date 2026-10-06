using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>
/// Отдельное окно партии — не мешает переписке и висит поверх, пока партия не закончится.
/// Закрыть посреди игры можно, только сдавшись.
/// </summary>
public partial class GameWindow : Window
{
    private static readonly Brush CrossBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly Brush NoughtBrush = new SolidColorBrush(Color.FromRgb(0xE1, 0x1D, 0x48));
    private static readonly Brush CellBrush = new SolidColorBrush(Color.FromRgb(0xF3, 0xF5, 0xF8));
    private static readonly Brush WinBrush = new SolidColorBrush(Color.FromRgb(0xFE, 0xF0, 0x8A));
    private static readonly Brush MyTurnBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly Brush NormalBrush = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));

    private readonly GameService _games;
    private readonly Button[] _cells = new Button[9];
    private bool _forceClose;

    public TicTacToeGame Game { get; }

    public GameWindow(GameService games, TicTacToeGame game)
    {
        InitializeComponent();
        Icon = AppIcon.CreateImageSource();
        _games = games;
        Game = game;

        for (var i = 0; i < 9; i++)
        {
            var cell = i;
            var button = new Button { Style = (Style)Resources["CellButton"] };
            button.Click += (_, _) => _games.Move(Game, cell);
            _cells[i] = button;
            Board.Children.Add(button);
        }

        Title = $"Крестики-нолики — {game.Opponent.Title}";
        PlayersText.Text = $"Вы — {game.MyMark}   ·   {game.Opponent.Title} — {game.OpponentMark}";

        game.Changed += Refresh;
        // Соперник пропал из сети или вернулся — обновляем подпись «ждём».
        PropertyChangedEventHandler onOpponentChanged = (_, _) => Refresh();
        game.Opponent.PropertyChanged += onOpponentChanged;
        Closed += (_, _) =>
        {
            game.Changed -= Refresh;
            game.Opponent.PropertyChanged -= onOpponentChanged;
        };
        Refresh();
    }

    /// <summary>Закрытие при выходе из программы: без вопросов, незаконченная партия считается сданной.</summary>
    public void ForceClose()
    {
        _forceClose = true;
        if (Game.State == GameState.Playing)
            _games.Resign(Game);
        else if (Game.State == GameState.Inviting)
            _games.CancelInvite(Game);
        Close();
    }

    private void Refresh()
    {
        for (var i = 0; i < 9; i++)
        {
            var mark = Game[i];
            var button = _cells[i];
            button.Content = mark == '\0' ? null : mark.ToString();
            button.Foreground = mark == TicTacToeGame.Cross ? CrossBrush : NoughtBrush;
            button.IsEnabled = Game.CanPlay(i);
            button.Background = Game.WinningLine?.Contains(i) == true ? WinBrush : CellBrush;
        }

        StatusText.Foreground = NormalBrush;
        var name = Game.Opponent.Title;
        switch (Game.State)
        {
            case GameState.Inviting:
                StatusText.Text = $"Ждём, пока {name} примет приглашение…";
                ActionButton.Content = "Отменить приглашение";
                break;

            case GameState.Playing when Game.IsMyTurn:
                StatusText.Text = "Ваш ход";
                StatusText.Foreground = MyTurnBrush;
                ActionButton.Content = "Сдаться";
                break;

            case GameState.Playing when Game.Opponent.IsOnline:
                StatusText.Text = $"Ходит {name}…";
                ActionButton.Content = "Сдаться";
                break;

            case GameState.Playing:
                StatusText.Text = $"{name} не в сети — ждём…";
                ActionButton.Content = "Прервать партию";
                break;

            case GameState.Finished:
                StatusText.Text = Game.Result switch
                {
                    GameResult.IWon when Game.EndedByResign => $"{name} сдался(-ась). Вы победили! 🏆",
                    GameResult.IWon => "Вы победили! 🏆",
                    GameResult.OpponentWon when Game.EndedByResign => "Вы сдались",
                    GameResult.OpponentWon => $"Победил(а) {name}",
                    _ => "Ничья 🤝",
                };
                ActionButton.Content = "Закрыть";
                // Партия закончилась — окну больше незачем висеть поверх всех.
                Topmost = false;
                break;

            case GameState.Aborted:
                StatusText.Text = Game.AbortReason;
                ActionButton.Content = "Закрыть";
                Topmost = false;
                break;
        }
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        switch (Game.State)
        {
            case GameState.Inviting:
                _games.CancelInvite(Game);
                Close();
                break;
            case GameState.Playing:
                ConfirmLeave();
                break;
            default:
                Close();
                break;
        }
    }

    /// <summary>
    /// Уйти из идущей партии: соперник в сети — только сдавшись,
    /// не в сети — можно прервать без результата.
    /// </summary>
    private bool ConfirmLeave()
    {
        var opponentOnline = Game.Opponent.IsOnline;
        var question = opponentOnline
            ? "Сдаться? Победа достанется сопернику."
            : $"{Game.Opponent.Title} не в сети. Прервать партию без результата?";
        if (MessageBox.Show(this, question, "Крестики-нолики",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return false;

        if (opponentOnline)
            _games.Resign(Game);
        else
            _games.Abandon(Game);
        return true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_forceClose) return;
        switch (Game.State)
        {
            // Окно висит до конца партии: закрыть можно, только сдавшись.
            case GameState.Playing when !ConfirmLeave():
                e.Cancel = true;
                break;
            case GameState.Inviting:
                _games.CancelInvite(Game);
                break;
        }
    }
}
