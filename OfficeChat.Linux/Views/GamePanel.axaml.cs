using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>
/// Партия (крестики-нолики, шашки или морской бой) внутри переписки — панель справа от ленты сообщений.
/// Видна, пока партия идёт; после окончания показывает итог до нажатия «Закрыть».
/// </summary>
public partial class GamePanel : UserControl
{
    private static readonly IBrush MyTurnBrush = new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly IBrush NormalBrush = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));

    private GameService? _games;
    private BoardGame? _game;
    private IGameBoard? _board;

    public GamePanel()
    {
        InitializeComponent();
    }

    public void Attach(GameService games) => _games = games;

    /// <summary>Показать партию (или скрыть панель, если null).</summary>
    public void Show(BoardGame? game)
    {
        if (_game != game)
        {
            if (_game != null)
            {
                _game.Changed -= Refresh;
                _game.Opponent.PropertyChanged -= OnOpponentChanged;
            }
            _game = game;
            _board = game == null || _games == null ? null : GameBoards.Create(game, _games);
            BoardHost.Content = _board?.View;
            if (_game != null)
            {
                _game.Changed += Refresh;
                // Соперник пропал из сети или вернулся — обновляем подпись «ждём».
                _game.Opponent.PropertyChanged += OnOpponentChanged;
            }
        }
        IsVisible = game != null;
        Refresh();
    }

    private void OnOpponentChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (_game is not { } game) return;
        _board?.Refresh();

        var name = game.Opponent.Title;
        TitleText.Text = $"🎮 {game.Title}";
        PlayersText.Text = game switch
        {
            BattleshipGame { State: GameState.Playing or GameState.Finished } sea when !sea.IsPlacing =>
                $"Кораблей на плаву: у вас {sea.MyShipsLeft}, у соперника {sea.EnemyShipsLeft}",
            CheckersGame checkers =>
                $"Вы — {game.MySide} · {name} — {game.OpponentSide}\nШашек: у вас {checkers.CountPieces(checkers.IAmWhite)}, " +
                $"у соперника {checkers.CountPieces(!checkers.IAmWhite)}",
            _ when game.MySide.Length > 0 => $"Вы — {game.MySide}  ·  {name} — {game.OpponentSide}",
            _ => $"Соперник — {name}",
        };
        StatusText.Foreground = NormalBrush;
        InviteButtons.IsVisible = false;
        PlacementButtons.IsVisible = false;
        ActionButton.IsVisible = true;

        switch (game.State)
        {
            case GameState.Inviting:
                StatusText.Text = $"Ждём, пока {name} примет приглашение…";
                ActionButton.Content = "Отменить приглашение";
                break;
            case GameState.Invited:
                StatusText.Text = $"{name} приглашает сыграть. {game.InviteHint}";
                InviteButtons.IsVisible = true;
                ActionButton.IsVisible = false;
                break;
            case GameState.Playing when game is BattleshipGame { IsPlacing: true } sea:
                StatusText.Text = !sea.IAmReady
                    ? "Расставьте корабли: «Перемешать» — другая расстановка, «Готов» — к бою."
                    : $"Ждём, пока {name} расставит корабли…";
                PlacementButtons.IsVisible = !sea.IAmReady;
                ActionButton.Content = "Сдаться";
                break;
            case GameState.Playing when game is BattleshipGame { PendingShot: not null }:
                StatusText.Text = "Выстрел… ждём ответа";
                ActionButton.Content = "Сдаться";
                break;
            case GameState.Playing when game.IsMyTurn:
                StatusText.Text = game switch
                {
                    BattleshipGame => "Ваш выстрел — выберите клетку на поле соперника",
                    CheckersGame { MustCapture: true } => "Ваш ход — нужно бить",
                    _ => "Ваш ход",
                };
                StatusText.Foreground = MyTurnBrush;
                ActionButton.Content = "Сдаться";
                break;
            case GameState.Playing when game.Opponent.IsOnline:
                StatusText.Text = game is BattleshipGame ? $"Стреляет {name}…" : $"Ходит {name}…";
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

    private void Accept_Click(object? sender, RoutedEventArgs e)
    {
        if (_game != null) _games?.Accept(_game);
    }

    private void Decline_Click(object? sender, RoutedEventArgs e)
    {
        if (_game != null) _games?.Decline(_game);
    }

    private void Shuffle_Click(object? sender, RoutedEventArgs e)
    {
        if (_game is BattleshipGame sea) sea.Shuffle();
    }

    private void Ready_Click(object? sender, RoutedEventArgs e)
    {
        if (_game is BattleshipGame sea) _games?.Ready(sea);
    }

    private async void Action_Click(object? sender, RoutedEventArgs e)
    {
        if (_game is not { } game || _games == null) return;
        switch (game.State)
        {
            case GameState.Inviting:
                _games.CancelInvite(game);
                break;
            case GameState.Playing:
                // Соперник в сети — уйти можно, только сдавшись; не в сети — прервать без результата.
                var online = game.Opponent.IsOnline;
                var question = online
                    ? "Сдаться? Победа достанется сопернику."
                    : $"{game.Opponent.Title} не в сети. Прервать партию без результата?";
                if (!await Dialogs.Confirm(TopLevel.GetTopLevel(this) as Window, question, game.Title)) return;
                if (online) _games.Resign(game);
                else _games.Abandon(game);
                break;
            default:
                _games.Dismiss(game);
                break;
        }
    }
}
