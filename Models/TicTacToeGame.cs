namespace OfficeChat.Models;

/// <summary>Партия в крестики-нолики. Пригласивший играет ✕ и ходит первым.</summary>
public sealed class TicTacToeGame : BoardGame
{
    public const char Cross = '✕';
    public const char Nought = '○';

    private static readonly int[][] Lines =
    {
        new[] { 0, 1, 2 }, new[] { 3, 4, 5 }, new[] { 6, 7, 8 }, // строки
        new[] { 0, 3, 6 }, new[] { 1, 4, 7 }, new[] { 2, 5, 8 }, // столбцы
        new[] { 0, 4, 8 }, new[] { 2, 4, 6 },                    // диагонали
    };

    /// <summary>Клетки: '\0' — пусто, иначе <see cref="Cross"/> или <see cref="Nought"/>.</summary>
    private readonly char[] _board = new char[9];

    public override GameKind Kind => GameKind.TicTacToe;

    /// <summary>Мы играем крестиками (то есть мы пригласили).</summary>
    public bool IAmCross => IAmFirst;

    /// <summary>Победная линия, если она есть.</summary>
    public int[]? WinningLine { get; private set; }

    public char MyMark => IAmCross ? Cross : Nought;
    public char OpponentMark => IAmCross ? Nought : Cross;

    public override string MySide => MyMark.ToString();
    public override string OpponentSide => OpponentMark.ToString();

    public override string InviteHint => "Вы будете играть ноликами (○), соперник ходит первым.";

    /// <summary>Крестики ходят на чётных ходах (0, 2, 4…), нолики — на нечётных.</summary>
    public override bool IsMyTurn => State == GameState.Playing && (MoveCount % 2 == 0) == IAmCross;

    public char this[int cell] => _board[cell];

    public bool CanPlay(int cell) => IsMyTurn && cell is >= 0 and < 9 && _board[cell] == '\0';

    /// <summary>
    /// Ставит знак того, чья сейчас очередь. Возвращает false, если ход невозможен
    /// (клетка занята, партия не идёт или номер хода не совпал).
    /// </summary>
    public bool ApplyMove(int cell, int moveNumber, bool byMe)
    {
        if (State != GameState.Playing || moveNumber != MoveCount) return false;
        if (cell is < 0 or > 8 || _board[cell] != '\0') return false;
        if (byMe != ((MoveCount % 2 == 0) == IAmCross)) return false;

        _board[cell] = byMe ? MyMark : OpponentMark;
        MoveCount++;

        foreach (var line in Lines)
        {
            var mark = _board[line[0]];
            if (mark != '\0' && mark == _board[line[1]] && mark == _board[line[2]])
            {
                WinningLine = line;
                Finish(mark == MyMark ? GameResult.IWon : GameResult.OpponentWon);
                return true;
            }
        }
        if (MoveCount == 9)
            Finish(GameResult.Draw);
        else
            RaiseChanged();
        return true;
    }

    public override string SummaryText() => BoardText();

    /// <summary>Доска текстом для истории переписки.</summary>
    public string BoardText()
    {
        var rows = new List<string>();
        for (var row = 0; row < 3; row++)
            rows.Add(string.Join("  ", Enumerable.Range(row * 3, 3).Select(i => _board[i] == '\0' ? '·' : _board[i])));
        return string.Join("\n", rows);
    }
}
