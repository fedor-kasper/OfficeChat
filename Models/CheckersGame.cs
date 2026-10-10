namespace OfficeChat.Models;

public enum CheckersPiece : byte
{
    None,
    White,
    WhiteKing,
    Black,
    BlackKing,
}

/// <summary>
/// Русские шашки. Доска 8×8, клетки 0…63 (ряд × 8 + столбец), играют на тёмных клетках.
/// Белые (пригласивший) внизу, ходят первыми. Правила:
/// - бить обязательно; простая шашка бьёт и вперёд, и назад; после взятия, если можно, бьёт дальше;
/// - из нескольких вариантов взятия можно выбрать любой (не обязательно самый длинный);
/// - сбитые шашки снимаются после хода, перепрыгнуть одну шашку дважды нельзя («турецкий удар»);
/// - дамка ходит и бьёт на любое расстояние; если после взятия она может бить дальше, встать
///   можно только на такую клетку;
/// - шашка, дошедшая до последнего ряда во время взятия, сразу становится дамкой и бьёт дальше как дамка;
/// - проигрывает тот, кому нечем или некуда ходить; 15 ходов подряд с каждой стороны только дамками
///   без взятий — ничья.
/// </summary>
public sealed class CheckersGame : BoardGame
{
    public const int Size = 8;

    // 15 ходов каждой стороны = 30 полуходов только дамками без взятий — ничья.
    private const int QuietKingMovesForDraw = 30;

    private static readonly (int Dr, int Dc)[] Directions = { (-1, -1), (-1, 1), (1, -1), (1, 1) };

    private readonly CheckersPiece[] _board = new CheckersPiece[Size * Size];
    private List<int[]> _legalMoves;
    private int _quietKingMoves;

    public CheckersGame()
    {
        for (var square = 0; square < _board.Length; square++)
        {
            if (!IsDark(square)) continue;
            var row = square / Size;
            if (row < 3) _board[square] = CheckersPiece.Black;
            else if (row > 4) _board[square] = CheckersPiece.White;
        }
        _legalMoves = Generate(white: true);
    }

    public override GameKind Kind => GameKind.Checkers;

    public bool IAmWhite => IAmFirst;

    public override string MySide => IAmWhite ? "белые" : "чёрные";
    public override string OpponentSide => IAmWhite ? "чёрные" : "белые";

    public override string InviteHint => "Вы будете играть чёрными, соперник (белые) ходит первым. Правила — русские шашки.";

    public bool WhiteToMove => MoveCount % 2 == 0;

    public override bool IsMyTurn => State == GameState.Playing && WhiteToMove == IAmWhite;

    public CheckersPiece this[int square] => _board[square];

    /// <summary>Ходы того, чья очередь: путь шашки по клеткам (начальная, затем каждая остановка).</summary>
    public IReadOnlyList<int[]> LegalMoves => _legalMoves;

    /// <summary>Есть ли сейчас обязательное взятие.</summary>
    public bool MustCapture => _legalMoves.Count > 0 && IsCaptureMove(_legalMoves[0]);

    /// <summary>Последний сделанный ход — подсвечивается на доске.</summary>
    public int[]? LastMove { get; private set; }

    public static bool IsDark(int square) => (square / Size + square % Size) % 2 == 1;

    public static bool IsWhite(CheckersPiece piece) => piece is CheckersPiece.White or CheckersPiece.WhiteKing;

    public static bool IsKing(CheckersPiece piece) => piece is CheckersPiece.WhiteKing or CheckersPiece.BlackKing;

    public int CountPieces(bool white) => _board.Count(p => p != CheckersPiece.None && IsWhite(p) == white);

    /// <summary>Наш ход: путь шашки. false — так ходить нельзя.</summary>
    public bool CanPlay(IReadOnlyList<int> path) => IsMyTurn && FindLegal(path) != null;

    /// <summary>
    /// Выполняет ход того, чья очередь. false — ход невозможен (не та очередь, не тот номер хода
    /// или такого хода нет по правилам).
    /// </summary>
    public bool ApplyMove(IReadOnlyList<int> path, int moveNumber, bool byMe)
    {
        if (State != GameState.Playing || moveNumber != MoveCount || byMe != IsMyTurn) return false;
        var move = FindLegal(path);
        if (move == null) return false;

        var piece = _board[move[0]];
        var wasKing = IsKing(piece);
        var captured = new List<int>();
        for (var i = 1; i < move.Length; i++)
            captured.AddRange(Between(move[i - 1], move[i]).Where(s => _board[s] != CheckersPiece.None));

        _board[move[0]] = CheckersPiece.None;
        foreach (var square in captured)
            _board[square] = CheckersPiece.None;
        // Шашка, побывавшая на последнем ряду (даже посреди взятия), — дамка.
        var white = IsWhite(piece);
        if (!IsKing(piece) && move.Skip(1).Any(s => PromotesAt(s, white)))
            piece = white ? CheckersPiece.WhiteKing : CheckersPiece.BlackKing;
        _board[move[^1]] = piece;

        _quietKingMoves = captured.Count == 0 && wasKing ? _quietKingMoves + 1 : 0;
        MoveCount++;
        LastMove = move;
        _legalMoves = Generate(WhiteToMove);

        if (_legalMoves.Count == 0)
            // Тому, чья очередь, нечем или некуда ходить — он проиграл.
            Finish(WhiteToMove == IAmWhite ? GameResult.OpponentWon : GameResult.IWon);
        else if (_quietKingMoves >= QuietKingMovesForDraw)
            Finish(GameResult.Draw);
        else
            RaiseChanged();
        return true;
    }

    public override string SummaryText() =>
        $"Ходов: {MoveCount}. Осталось шашек: у вас {CountPieces(IAmWhite)}, у соперника {CountPieces(!IAmWhite)}.";

    // ---- Правила ----

    private int[]? FindLegal(IReadOnlyList<int> path) =>
        _legalMoves.FirstOrDefault(m => m.Length == path.Count && m.SequenceEqual(path));

    private bool IsCaptureMove(int[] move) =>
        move.Length > 2 || Between(move[0], move[1]).Any(s => _board[s] != CheckersPiece.None);

    private static bool PromotesAt(int square, bool white) => square / Size == (white ? 0 : Size - 1);

    /// <summary>Клетка на расстоянии <paramref name="distance"/> по диагонали; -1 — за краем доски.</summary>
    private static int Step(int square, int dr, int dc, int distance)
    {
        var row = square / Size + dr * distance;
        var col = square % Size + dc * distance;
        return row is >= 0 and < Size && col is >= 0 and < Size ? row * Size + col : -1;
    }

    /// <summary>Клетки строго между двумя клетками одной диагонали.</summary>
    private static IEnumerable<int> Between(int from, int to)
    {
        var dr = Math.Sign(to / Size - from / Size);
        var dc = Math.Sign(to % Size - from % Size);
        for (var square = Step(from, dr, dc, 1); square != to && square >= 0; square = Step(square, dr, dc, 1))
            yield return square;
    }

    private bool IsOpponent(int square, bool white) =>
        _board[square] != CheckersPiece.None && IsWhite(_board[square]) != white;

    /// <summary>Свободна ли клетка во время хода: клетка, с которой шашка ушла, считается пустой.</summary>
    private bool IsFree(int square, int origin) => square == origin || _board[square] == CheckersPiece.None;

    private List<int[]> Generate(bool white)
    {
        var captures = new List<int[]>();
        for (var square = 0; square < _board.Length; square++)
        {
            var piece = _board[square];
            if (piece == CheckersPiece.None || IsWhite(piece) != white) continue;
            ExtendCaptures(square, IsKing(piece), white, square, new List<int> { square }, new HashSet<int>(), captures);
        }
        if (captures.Count > 0) return captures;

        var moves = new List<int[]>();
        for (var square = 0; square < _board.Length; square++)
        {
            var piece = _board[square];
            if (piece == CheckersPiece.None || IsWhite(piece) != white) continue;
            foreach (var (dr, dc) in Directions)
            {
                if (IsKing(piece))
                {
                    for (var d = 1; Step(square, dr, dc, d) is var target and >= 0 && _board[target] == CheckersPiece.None; d++)
                        moves.Add(new[] { square, target });
                }
                else if (dr == (white ? -1 : 1) && Step(square, dr, dc, 1) is var target and >= 0 &&
                         _board[target] == CheckersPiece.None)
                {
                    moves.Add(new[] { square, target });
                }
            }
        }
        return moves;
    }

    /// <summary>
    /// Перебирает все цепочки взятий от <paramref name="from"/>. Законченные (дальше бить нечего)
    /// добавляет в <paramref name="result"/>. Сбитые шашки остаются на доске до конца хода.
    /// </summary>
    private void ExtendCaptures(int from, bool king, bool white, int origin, List<int> path, HashSet<int> captured,
        List<int[]> result)
    {
        var extended = false;
        foreach (var (dr, dc) in Directions)
        {
            if (!king)
            {
                var victim = Step(from, dr, dc, 1);
                var land = Step(from, dr, dc, 2);
                if (victim < 0 || land < 0 || !IsOpponent(victim, white) || captured.Contains(victim) ||
                    !IsFree(land, origin))
                    continue;

                extended = true;
                captured.Add(victim);
                path.Add(land);
                ExtendCaptures(land, PromotesAt(land, white), white, origin, path, captured, result);
                path.RemoveAt(path.Count - 1);
                captured.Remove(victim);
                continue;
            }

            // Дамка: скользим до первой занятой клетки — это должна быть чужая, ещё не сбитая шашка.
            var distance = 1;
            int square;
            while ((square = Step(from, dr, dc, distance)) >= 0 && IsFree(square, origin))
                distance++;
            if (square < 0 || !IsOpponent(square, white) || captured.Contains(square)) continue;
            var target = square;

            var landings = new List<int>();
            for (var d = distance + 1; (square = Step(from, dr, dc, d)) >= 0 && IsFree(square, origin); d++)
                landings.Add(square);
            if (landings.Count == 0) continue;

            extended = true;
            captured.Add(target);
            // Если с какой-то из клеток можно бить дальше — встать можно только на такие.
            var continuing = landings.Where(l => CanKingCapture(l, white, origin, captured)).ToList();
            foreach (var land in continuing.Count > 0 ? continuing : landings)
            {
                path.Add(land);
                ExtendCaptures(land, true, white, origin, path, captured, result);
                path.RemoveAt(path.Count - 1);
            }
            captured.Remove(target);
        }

        if (!extended && path.Count > 1)
            result.Add(path.ToArray());
    }

    private bool CanKingCapture(int from, bool white, int origin, HashSet<int> captured)
    {
        foreach (var (dr, dc) in Directions)
        {
            var distance = 1;
            int square;
            while ((square = Step(from, dr, dc, distance)) >= 0 && IsFree(square, origin))
                distance++;
            if (square < 0 || !IsOpponent(square, white) || captured.Contains(square)) continue;
            var land = Step(from, dr, dc, distance + 1);
            if (land >= 0 && IsFree(land, origin)) return true;
        }
        return false;
    }
}
