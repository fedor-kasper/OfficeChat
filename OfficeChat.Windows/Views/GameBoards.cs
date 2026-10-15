using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>Поле игры внутри панели партии: строится в коде и перерисовывается при каждом изменении.</summary>
public interface IGameBoard
{
    FrameworkElement View { get; }
    void Refresh();
}

public static class GameBoards
{
    public static IGameBoard Create(BoardGame game, GameService games, Style cellButtonStyle) => game switch
    {
        CheckersGame checkers => new CheckersBoard(checkers, games),
        BattleshipGame battleship => new BattleshipBoard(battleship, games),
        _ => new TicTacToeBoard((TicTacToeGame)game, games, cellButtonStyle),
    };

    public static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

/// <summary>Крестики-нолики: 3×3 кнопки.</summary>
internal sealed class TicTacToeBoard : IGameBoard
{
    private static readonly Brush CrossBrush = GameBoards.Frozen(0x25, 0x63, 0xEB);
    private static readonly Brush NoughtBrush = GameBoards.Frozen(0xE1, 0x1D, 0x48);
    private static readonly Brush WinBrush = GameBoards.Frozen(0xFE, 0xF0, 0x8A);

    private readonly TicTacToeGame _game;
    private readonly Button[] _cells = new Button[9];

    public FrameworkElement View { get; }

    public TicTacToeBoard(TicTacToeGame game, GameService games, Style cellStyle)
    {
        _game = game;
        var grid = new UniformGrid { Rows = 3, Columns = 3, Width = 216, Height = 216 };
        for (var i = 0; i < 9; i++)
        {
            var cell = i;
            var button = new Button { Style = cellStyle };
            button.Click += (_, _) => games.Move(_game, cell);
            _cells[i] = button;
            grid.Children.Add(button);
        }
        View = grid;
    }

    public void Refresh()
    {
        for (var i = 0; i < 9; i++)
        {
            var mark = _game[i];
            var button = _cells[i];
            button.Content = mark == '\0' ? null : mark.ToString();
            button.Foreground = mark == TicTacToeGame.Cross ? CrossBrush : NoughtBrush;
            button.IsEnabled = _game.CanPlay(i);
            button.Background = _game.WinningLine?.Contains(i) == true ? WinBrush : Brushes.White;
        }
    }
}

/// <summary>
/// Шашки: доска 8×8, свои шашки всегда внизу. Клик по своей шашке выбирает её, затем клик по клетке —
/// ход; при взятии нескольких шашек — по каждой клетке остановки по очереди.
/// </summary>
internal sealed class CheckersBoard : IGameBoard
{
    private const double CellSize = 34;

    private static readonly Brush LightBrush = GameBoards.Frozen(0xF1, 0xE4, 0xC8);
    private static readonly Brush DarkBrush = GameBoards.Frozen(0xA9, 0x7C, 0x50);
    private static readonly Brush LastMoveBrush = GameBoards.Frozen(0xC9, 0xA2, 0x5F);
    private static readonly Brush SelectedBrush = GameBoards.Frozen(0xFA, 0xCC, 0x15);
    private static readonly Brush TargetBrush = GameBoards.Frozen(0x22, 0xC5, 0x5E);
    private static readonly Brush WhitePieceBrush = GameBoards.Frozen(0xFA, 0xFA, 0xF9);
    private static readonly Brush BlackPieceBrush = GameBoards.Frozen(0x29, 0x25, 0x24);
    private static readonly Brush OutlineBrush = GameBoards.Frozen(0x44, 0x40, 0x3C);

    private readonly CheckersGame _game;
    private readonly GameService _games;
    private readonly Border[] _squares = new Border[64];
    private readonly Ellipse[] _pieces = new Ellipse[64];
    private readonly TextBlock[] _crowns = new TextBlock[64];
    private readonly Ellipse[] _targets = new Ellipse[64];
    // Путь, выбранный кликами: шашка и уже выбранные клетки остановок.
    private readonly List<int> _path = new();

    public FrameworkElement View { get; }

    public CheckersBoard(CheckersGame game, GameService games)
    {
        _game = game;
        _games = games;
        var grid = new UniformGrid { Rows = 8, Columns = 8, Width = CellSize * 8, Height = CellSize * 8 };
        for (var index = 0; index < 64; index++)
        {
            // Свои шашки — внизу: чёрным доска показывается перевёрнутой.
            var square = _game.IAmWhite ? index : 63 - index;
            var piece = new Ellipse { Margin = new Thickness(4), StrokeThickness = 1.5, Stroke = OutlineBrush };
            var crown = new TextBlock
            {
                Text = "♛", FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var target = new Ellipse { Width = 12, Height = 12, Fill = TargetBrush, IsHitTestVisible = false };
            var cell = new Grid { Children = { piece, crown, target } };
            var border = new Border
            {
                Background = CheckersGame.IsDark(square) ? DarkBrush : LightBrush,
                Child = cell,
            };
            border.MouseLeftButtonUp += (_, _) => OnClick(square);
            _squares[square] = border;
            _pieces[square] = piece;
            _crowns[square] = crown;
            _targets[square] = target;
            grid.Children.Add(border);
        }
        View = new Border { BorderBrush = OutlineBrush, BorderThickness = new Thickness(2), Child = grid };
    }

    private IEnumerable<int[]> Candidates => _game.LegalMoves.Where(m => m.Length > _path.Count && m.Take(_path.Count).SequenceEqual(_path));

    private void OnClick(int square)
    {
        if (!_game.IsMyTurn)
        {
            _path.Clear();
            Refresh();
            return;
        }

        // Выбор шашки (или смена выбора, пока шашка ещё не начала ход).
        if (_path.Count <= 1 && _game.LegalMoves.Any(m => m[0] == square))
        {
            _path.Clear();
            _path.Add(square);
            Refresh();
            return;
        }

        if (_path.Count > 0)
        {
            var next = Candidates.Where(m => m[_path.Count] == square).ToList();
            if (next.Count > 0)
            {
                _path.Add(square);
                var complete = next.FirstOrDefault(m => m.Length == _path.Count);
                if (complete != null)
                {
                    _path.Clear();
                    _games.Move(_game, complete);
                }
                Refresh();
                return;
            }
        }
        // Клик мимо — снимаем выбор (посреди взятия выбор сохраняется: ход надо закончить).
        if (_path.Count <= 1) _path.Clear();
        Refresh();
    }

    public void Refresh()
    {
        if (!_game.IsMyTurn) _path.Clear();
        var targets = _path.Count > 0 ? Candidates.Select(m => m[_path.Count]).ToHashSet() : new HashSet<int>();
        var movable = _path.Count == 0 && _game.IsMyTurn ? _game.LegalMoves.Select(m => m[0]).ToHashSet() : new HashSet<int>();
        for (var square = 0; square < 64; square++)
        {
            var piece = _game[square];
            var dark = CheckersGame.IsDark(square);
            _squares[square].Background = !dark ? LightBrush
                : _path.Contains(square) ? SelectedBrush
                : _game.LastMove?.Contains(square) == true ? LastMoveBrush
                : DarkBrush;
            _squares[square].Cursor = movable.Contains(square) || targets.Contains(square) || _path.Contains(square)
                ? Cursors.Hand
                : null;

            _pieces[square].Visibility = piece == CheckersPiece.None ? Visibility.Collapsed : Visibility.Visible;
            _pieces[square].Fill = CheckersGame.IsWhite(piece) ? WhitePieceBrush : BlackPieceBrush;
            // Шашки, которыми можно ходить, обведены ярче — особенно важно, когда бить обязательно.
            _pieces[square].Stroke = movable.Contains(square) && _game.MustCapture ? SelectedBrush : OutlineBrush;
            _pieces[square].StrokeThickness = movable.Contains(square) && _game.MustCapture ? 3 : 1.5;
            _crowns[square].Visibility = CheckersGame.IsKing(piece) ? Visibility.Visible : Visibility.Collapsed;
            _crowns[square].Foreground = CheckersGame.IsWhite(piece) ? BlackPieceBrush : WhitePieceBrush;
            _targets[square].Visibility = targets.Contains(square) ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}

/// <summary>
/// Морской бой: при расстановке — свой флот крупно; в бою — поле соперника (куда стрелять)
/// и своё поле поменьше (куда попал соперник).
/// </summary>
internal sealed class BattleshipBoard : IGameBoard
{
    private const double BigCell = 22;
    private const double SmallCell = 14;

    private static readonly Brush WaterBrush = GameBoards.Frozen(0xE0, 0xF2, 0xFE);
    private static readonly Brush GridLineBrush = GameBoards.Frozen(0xBA, 0xE6, 0xFD);
    private static readonly Brush ShipBrush = GameBoards.Frozen(0x64, 0x74, 0x8B);
    private static readonly Brush HitBrush = GameBoards.Frozen(0xF8, 0x71, 0x71);
    private static readonly Brush SunkBrush = GameBoards.Frozen(0xB9, 0x1C, 0x1C);
    private static readonly Brush PendingBrush = GameBoards.Frozen(0xFD, 0xE0, 0x47);
    private static readonly Brush AimBrush = GameBoards.Frozen(0xBF, 0xDB, 0xFE);
    private static readonly Brush MissBrush = GameBoards.Frozen(0x64, 0x74, 0x8B);

    private readonly BattleshipGame _game;
    private readonly (Border Cell, TextBlock Mark)[] _enemy;
    private readonly (Border Cell, TextBlock Mark)[] _mine;
    private readonly (Border Cell, TextBlock Mark)[] _placement;
    private readonly FrameworkElement _battleView;
    private readonly FrameworkElement _placementView;
    private readonly ContentControl _host = new();

    public FrameworkElement View => _host;

    public BattleshipBoard(BattleshipGame game, GameService games)
    {
        _game = game;
        _enemy = CreateField(BigCell, out var enemyGrid, cell => games.Shoot(_game, cell));
        _mine = CreateField(SmallCell, out var myGrid, null);
        _placement = CreateField(BigCell, out var placementGrid, null);

        _battleView = new StackPanel
        {
            Children =
            {
                Caption("Поле соперника"),
                enemyGrid,
                Caption("Ваш флот"),
                myGrid,
            },
        };
        _placementView = new StackPanel { Children = { Caption("Ваш флот"), placementGrid } };
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text, FontSize = 12, Margin = new Thickness(0, 6, 0, 4),
        Foreground = GameBoards.Frozen(0x6B, 0x72, 0x80),
    };

    private (Border, TextBlock)[] CreateField(double size, out FrameworkElement view, Action<int>? click)
    {
        var cells = new (Border, TextBlock)[100];
        var grid = new UniformGrid { Rows = 10, Columns = 10, Width = size * 10, Height = size * 10 };
        for (var cell = 0; cell < 100; cell++)
        {
            var index = cell;
            var mark = new TextBlock
            {
                FontSize = size * 0.45, FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            var border = new Border
            {
                Background = WaterBrush, BorderBrush = GridLineBrush, BorderThickness = new Thickness(0.5), Child = mark,
            };
            if (click != null)
            {
                border.MouseLeftButtonUp += (_, _) => click(index);
                border.MouseEnter += (_, _) =>
                {
                    if (_game.CanShoot(index)) border.Background = AimBrush;
                };
                border.MouseLeave += (_, _) => Refresh();
            }
            cells[cell] = (border, mark);
            grid.Children.Add(border);
        }
        view = new Border { BorderBrush = GridLineBrush, BorderThickness = new Thickness(1), Child = grid, HorizontalAlignment = HorizontalAlignment.Center };
        return cells;
    }

    public void Refresh()
    {
        var placing = _game.IsPlacing || _game.State is GameState.Inviting or GameState.Invited;
        _host.Content = placing ? _placementView : _battleView;

        for (var cell = 0; cell < 100; cell++)
        {
            // Расстановка: только корабли.
            var (placeCell, placeMark) = _placement[cell];
            placeCell.Background = _game.HasMyShipAt(cell) ? ShipBrush : WaterBrush;
            placeMark.Text = "";

            // Своё поле в бою: корабли и куда попал соперник.
            var (myCell, myMark) = _mine[cell];
            var mine = _game.MyField(cell);
            myCell.Background = mine switch
            {
                SeaMark.Sunk => SunkBrush,
                SeaMark.Hit => HitBrush,
                _ when _game.HasMyShipAt(cell) => ShipBrush,
                _ => WaterBrush,
            };
            myMark.Text = mine == SeaMark.Miss ? "●" : "";
            myMark.Foreground = MissBrush;

            // Поле соперника: наши выстрелы.
            var (enemyCell, enemyMark) = _enemy[cell];
            var enemy = _game.EnemyField(cell);
            enemyCell.Background = enemy switch
            {
                SeaMark.Sunk => SunkBrush,
                SeaMark.Hit => HitBrush,
                _ when _game.PendingShot == cell => PendingBrush,
                _ => WaterBrush,
            };
            enemyMark.Text = enemy switch { SeaMark.Miss => "●", SeaMark.Hit or SeaMark.Sunk => "✕", _ => "" };
            enemyMark.Foreground = enemy == SeaMark.Miss ? MissBrush : Brushes.White;
            enemyCell.Cursor = _game.CanShoot(cell) ? Cursors.Hand : null;
        }
    }
}
