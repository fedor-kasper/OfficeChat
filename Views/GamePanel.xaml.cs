using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>
/// Партия в крестики-нолики внутри переписки — панель справа от ленты сообщений.
/// Видна, пока партия идёт; после окончания показывает итог до нажатия «Закрыть».
/// </summary>
public partial class GamePanel : UserControl
{
    private static readonly Brush CrossBrush = Frozen(0x25, 0x63, 0xEB);
    private static readonly Brush NoughtBrush = Frozen(0xE1, 0x1D, 0x48);
    private static readonly Brush CellBrush = Brushes.White;
    private static readonly Brush WinBrush = Frozen(0xFE, 0xF0, 0x8A);
    private static readonly Brush MyTurnBrush = Frozen(0x16, 0xA3, 0x4A);
    private static readonly Brush NormalBrush = Frozen(0x11, 0x18, 0x27);

    private readonly Button[] _cells = new Button[9];
    private GameService? _games;
    private TicTacToeGame? _game;

    public GamePanel()
    {
        InitializeComponent();
        for (var i = 0; i < 9; i++)
        {
            var cell = i;
            var button = new Button { Style = (Style)Resources["CellButton"] };
            button.Click += (_, _) =>
            {
                if (_game != null) _games?.Move(_game, cell);
            };
            _cells[i] = button;
            Board.Children.Add(button);
        }
    }

    public void Attach(GameService games) => _games = games;

    /// <summary>Показать партию (или скрыть панель, если null).</summary>
    public void Show(TicTacToeGame? game)
    {
        if (_game != game)
        {
            if (_game != null)
            {
                _game.Changed -= Refresh;
                _game.Opponent.PropertyChanged -= OnOpponentChanged;
            }
            _game = game;
            if (_game != null)
            {
                _game.Changed += Refresh;
                // Соперник пропал из сети или вернулся — обновляем подпись «ждём».
                _game.Opponent.PropertyChanged += OnOpponentChanged;
            }
        }

        Visibility = game == null ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }

    private void OnOpponentChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (_game is not { } game) return;

        for (var i = 0; i < 9; i++)
        {
            var mark = game[i];
            var button = _cells[i];
            button.Content = mark == '\0' ? null : mark.ToString();
            button.Foreground = mark == TicTacToeGame.Cross ? CrossBrush : NoughtBrush;
            button.IsEnabled = game.CanPlay(i);
            button.Background = game.WinningLine?.Contains(i) == true ? WinBrush : CellBrush;
        }

        var name = game.Opponent.Title;
        PlayersText.Text = $"Вы — {game.MyMark}  ·  {name} — {game.OpponentMark}";
        StatusText.Foreground = NormalBrush;
        InviteButtons.Visibility = Visibility.Collapsed;
        ActionButton.Visibility = Visibility.Visible;

        switch (game.State)
        {
            case GameState.Inviting:
                StatusText.Text = $"Ждём, пока {name} примет приглашение…";
                ActionButton.Content = "Отменить приглашение";
                break;

            case GameState.Invited:
                StatusText.Text = $"{name} приглашает сыграть. Вы — ноликами, соперник ходит первым.";
                InviteButtons.Visibility = Visibility.Visible;
                ActionButton.Visibility = Visibility.Collapsed;
                break;

            case GameState.Playing when game.IsMyTurn:
                StatusText.Text = "Ваш ход";
                StatusText.Foreground = MyTurnBrush;
                ActionButton.Content = "Сдаться";
                break;

            case GameState.Playing when game.Opponent.IsOnline:
                StatusText.Text = $"Ходит {name}…";
                ActionButton.Content = "Сдаться";
                break;

            case GameState.Playing:
                StatusText.Text = $"{name} не в сети — ждём…";
                ActionButton.Content = "Прервать партию";
                break;

            case GameState.Finished:
                StatusText.Text = game.Result switch
                {
                    GameResult.IWon when game.EndedByResign => $"{name} сдался(-ась). Вы победили! 🏆",
                    GameResult.IWon => "Вы победили! 🏆",
                    GameResult.OpponentWon when game.EndedByResign => "Вы сдались",
                    GameResult.OpponentWon => $"Победил(а) {name}",
                    _ => "Ничья 🤝",
                };
                if (game.Result == GameResult.IWon) StatusText.Foreground = MyTurnBrush;
                ActionButton.Content = "Закрыть";
                break;

            case GameState.Aborted:
                StatusText.Text = game.AbortReason;
                ActionButton.Content = "Закрыть";
                break;
        }
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (_game != null) _games?.Accept(_game);
    }

    private void Decline_Click(object sender, RoutedEventArgs e)
    {
        if (_game != null) _games?.Decline(_game);
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game || _games == null) return;
        switch (game.State)
        {
            case GameState.Inviting:
                _games.CancelInvite(game);
                break;
            case GameState.Playing:
                ConfirmLeave(game);
                break;
            default:
                _games.Dismiss(game);
                break;
        }
    }

    /// <summary>
    /// Уйти из идущей партии: соперник в сети — только сдавшись,
    /// не в сети — можно прервать без результата.
    /// </summary>
    private void ConfirmLeave(TicTacToeGame game)
    {
        var opponentOnline = game.Opponent.IsOnline;
        var question = opponentOnline
            ? "Сдаться? Победа достанется сопернику."
            : $"{game.Opponent.Title} не в сети. Прервать партию без результата?";
        if (MessageBox.Show(Window.GetWindow(this)!, question, "Крестики-нолики",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        if (opponentOnline)
            _games!.Resign(game);
        else
            _games!.Abandon(game);
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
