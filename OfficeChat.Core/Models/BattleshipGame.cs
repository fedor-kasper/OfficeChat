namespace OfficeChat.Models;

/// <summary>Отметка на поле: куда стреляли и что вышло.</summary>
public enum SeaMark : byte
{
    None,
    Miss,
    Hit,
    Sunk,
}

/// <summary>Что ответил соперник на выстрел.</summary>
public enum ShotOutcome
{
    Miss = 0,
    Hit = 1,
    Sunk = 2,
    /// <summary>Потоплен последний корабль — победа стрелявшего.</summary>
    SunkLast = 3,
}

/// <summary>
/// Морской бой. Поле 10×10, клетки 0…99 (ряд × 10 + столбец). Флот: 4-палубный, два 3-палубных,
/// три 2-палубных и четыре 1-палубных; корабли не касаются друг друга даже углами.
/// Сначала каждый расставляет флот (случайно, можно перемешать) и жмёт «Готов»; первым стреляет пригласивший.
/// Попал — стреляешь ещё раз, промахнулся — ход соперника. Свой флот знает только его хозяин:
/// на выстрел отвечает компьютер соперника.
/// </summary>
public sealed class BattleshipGame : BoardGame
{
    public const int Size = 10;
    public static readonly int[] FleetLengths = { 4, 3, 3, 2, 2, 2, 1, 1, 1, 1 };

    private List<int[]> _myShips = RandomFleet(Random.Shared);
    // Выстрелы соперника по нашему полю и наши — по его полю.
    private readonly SeaMark[] _myField = new SeaMark[Size * Size];
    private readonly SeaMark[] _enemyField = new SeaMark[Size * Size];
    private bool _myTurn;

    public override GameKind Kind => GameKind.Battleship;

    public override string MySide => "";
    public override string OpponentSide => "";

    public override string InviteHint =>
        "Корабли расставятся сами, расстановку можно перемешать. Первым стреляет соперник.";

    /// <summary>Мы расставили флот и нажали «Готов».</summary>
    public bool IAmReady { get; private set; }

    public bool OpponentReady { get; private set; }

    /// <summary>Идёт расстановка (кто-то ещё не готов).</summary>
    public bool IsPlacing => State == GameState.Playing && !(IAmReady && OpponentReady);

    public bool InBattle => State == GameState.Playing && IAmReady && OpponentReady;

    /// <summary>Наш выстрел ждёт ответа соперника — стрелять снова пока нельзя.</summary>
    public int? PendingShot { get; private set; }

    public override bool IsMyTurn => InBattle && _myTurn && PendingShot == null;

    /// <summary>Чья очередь стрелять (даже пока ждём ответа на свой выстрел).</summary>
    public bool MyTurnToShoot => InBattle && _myTurn;

    public int MyShots { get; private set; }
    public int MyHits { get; private set; }

    public IReadOnlyList<int[]> MyShips => _myShips;

    public bool HasMyShipAt(int cell) => _myShips.Any(s => s.Contains(cell));

    public SeaMark MyField(int cell) => _myField[cell];

    public SeaMark EnemyField(int cell) => _enemyField[cell];

    /// <summary>Сколько кораблей соперника ещё на плаву.</summary>
    public int EnemyShipsLeft =>
        FleetLengths.Length - CountSunkShips(_enemyField);

    public int MyShipsLeft => _myShips.Count(s => s.Any(c => _myField[c] != SeaMark.Sunk));

    // ---- Расстановка ----

    /// <summary>Расставить флот заново, пока не нажали «Готов».</summary>
    public void Shuffle()
    {
        if (IAmReady || State != GameState.Playing) return;
        _myShips = RandomFleet(Random.Shared);
        RaiseChanged();
    }

    public void SetReady()
    {
        if (IAmReady || State != GameState.Playing) return;
        IAmReady = true;
        StartIfBothReady();
        RaiseChanged();
    }

    public void SetOpponentReady()
    {
        if (OpponentReady || State != GameState.Playing) return;
        OpponentReady = true;
        StartIfBothReady();
        RaiseChanged();
    }

    private void StartIfBothReady()
    {
        if (IAmReady && OpponentReady) _myTurn = IAmFirst;
    }

    // ---- Бой ----

    public bool CanShoot(int cell) => IsMyTurn && cell is >= 0 and < Size * Size && _enemyField[cell] == SeaMark.None;

    /// <summary>Наш выстрел: запоминаем клетку до ответа соперника. Номер выстрела — <see cref="BoardGame.MoveCount"/>.</summary>
    public bool BeginShot(int cell)
    {
        if (!CanShoot(cell)) return false;
        PendingShot = cell;
        RaiseChanged();
        return true;
    }

    /// <summary>Ответ соперника на наш выстрел. false — ответ не к месту (повтор, чужой номер).</summary>
    public bool ApplyShotResult(int cell, int shotNumber, ShotOutcome outcome, IReadOnlyList<int>? sunkShip)
    {
        if (PendingShot != cell || shotNumber != MoveCount) return false;
        PendingShot = null;
        MoveCount++;
        MyShots++;

        if (outcome == ShotOutcome.Miss)
        {
            _enemyField[cell] = SeaMark.Miss;
            _myTurn = false;
            RaiseChanged();
            return true;
        }

        MyHits++;
        _enemyField[cell] = SeaMark.Hit;
        if (outcome is ShotOutcome.Sunk or ShotOutcome.SunkLast && sunkShip != null)
        {
            // Вокруг потопленного корабля кораблей быть не может — отмечаем эти клетки, чтобы туда не стрелять.
            foreach (var part in sunkShip.Where(c => c is >= 0 and < Size * Size))
            {
                _enemyField[part] = SeaMark.Sunk;
                foreach (var around in Neighbours(part).Where(n => _enemyField[n] == SeaMark.None))
                    _enemyField[around] = SeaMark.Miss;
            }
        }
        if (outcome == ShotOutcome.SunkLast)
            Finish(GameResult.IWon);
        else
            RaiseChanged();
        return true;
    }

    /// <summary>
    /// Выстрел соперника по нашему полю. Возвращает ответ для него (и клетки корабля, если он потоплен);
    /// null — выстрел не к месту (не его очередь, повтор).
    /// </summary>
    public (ShotOutcome Outcome, int[]? SunkShip)? ApplyOpponentShot(int cell, int shotNumber)
    {
        if (!InBattle || _myTurn || shotNumber != MoveCount || cell is < 0 or >= Size * Size ||
            _myField[cell] != SeaMark.None)
            return null;
        MoveCount++;

        var ship = _myShips.FirstOrDefault(s => s.Contains(cell));
        if (ship == null)
        {
            _myField[cell] = SeaMark.Miss;
            _myTurn = true;
            RaiseChanged();
            return (ShotOutcome.Miss, null);
        }

        _myField[cell] = SeaMark.Hit;
        if (ship.Any(c => _myField[c] == SeaMark.None))
        {
            RaiseChanged();
            return (ShotOutcome.Hit, null);
        }

        foreach (var part in ship)
            _myField[part] = SeaMark.Sunk;
        if (_myShips.All(s => s.All(c => _myField[c] == SeaMark.Sunk)))
        {
            Finish(GameResult.OpponentWon);
            return (ShotOutcome.SunkLast, ship);
        }
        RaiseChanged();
        return (ShotOutcome.Sunk, ship);
    }

    public override string SummaryText() =>
        $"Выстрелов: {MyShots}, попаданий: {MyHits}. Ваших кораблей на плаву: {MyShipsLeft}, у соперника: {EnemyShipsLeft}.";

    // ---- Флот ----

    private static int CountSunkShips(SeaMark[] field)
    {
        // Потопленные корабли — связные (по горизонтали и вертикали) группы клеток «потоплен».
        var seen = new bool[field.Length];
        var count = 0;
        for (var cell = 0; cell < field.Length; cell++)
        {
            if (field[cell] != SeaMark.Sunk || seen[cell]) continue;
            count++;
            var stack = new Stack<int>();
            stack.Push(cell);
            seen[cell] = true;
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                foreach (var next in Orthogonal(current))
                {
                    if (field[next] != SeaMark.Sunk || seen[next]) continue;
                    seen[next] = true;
                    stack.Push(next);
                }
            }
        }
        return count;
    }

    /// <summary>Случайная расстановка: корабли не выходят за поле и не касаются друг друга даже углами.</summary>
    public static List<int[]> RandomFleet(Random random)
    {
        while (true)
        {
            var ships = new List<int[]>();
            var blocked = new bool[Size * Size];
            var placedAll = true;
            foreach (var length in FleetLengths)
            {
                int[]? ship = null;
                for (var attempt = 0; attempt < 200 && ship == null; attempt++)
                {
                    var horizontal = random.Next(2) == 0;
                    var row = random.Next(horizontal ? Size : Size - length + 1);
                    var col = random.Next(horizontal ? Size - length + 1 : Size);
                    var cells = Enumerable.Range(0, length)
                        .Select(i => horizontal ? row * Size + col + i : (row + i) * Size + col)
                        .ToArray();
                    if (cells.All(c => !blocked[c])) ship = cells;
                }
                if (ship == null)
                {
                    placedAll = false;
                    break;
                }
                ships.Add(ship);
                foreach (var cell in ship)
                {
                    blocked[cell] = true;
                    foreach (var around in Neighbours(cell))
                        blocked[around] = true;
                }
            }
            if (placedAll) return ships;
        }
    }

    /// <summary>Соседние клетки, включая диагональные.</summary>
    public static IEnumerable<int> Neighbours(int cell)
    {
        var row = cell / Size;
        var col = cell % Size;
        for (var dr = -1; dr <= 1; dr++)
        for (var dc = -1; dc <= 1; dc++)
        {
            if (dr == 0 && dc == 0) continue;
            var r = row + dr;
            var c = col + dc;
            if (r is >= 0 and < Size && c is >= 0 and < Size) yield return r * Size + c;
        }
    }

    private static IEnumerable<int> Orthogonal(int cell)
    {
        var row = cell / Size;
        var col = cell % Size;
        if (row > 0) yield return cell - Size;
        if (row < Size - 1) yield return cell + Size;
        if (col > 0) yield return cell - 1;
        if (col < Size - 1) yield return cell + 1;
    }
}
