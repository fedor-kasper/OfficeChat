namespace OfficeChat.Models;

public enum GameState
{
    /// <summary>Мы пригласили, ждём ответа.</summary>
    Inviting,
    /// <summary>Нас пригласили, мы ещё не ответили.</summary>
    Invited,
    Playing,
    Finished,
    /// <summary>Приглашение отклонено или отменено — партии не было.</summary>
    Aborted,
}

public enum GameResult
{
    None,
    IWon,
    OpponentWon,
    Draw,
}

public enum GameKind
{
    TicTacToe,
    Checkers,
    Battleship,
}

/// <summary>
/// Партия с одним собеседником: приглашение, очерёдность, сдача, итог. Пригласивший ходит первым.
/// Правила конкретной игры — в наследниках; по сети ходы передаёт GameService.
/// </summary>
public abstract class BoardGame
{
    public required Guid Id { get; init; }
    public required Contact Opponent { get; init; }

    /// <summary>Мы пригласили — значит, ходим первыми.</summary>
    public required bool IAmFirst { get; init; }

    public GameState State { get; set; }
    public GameResult Result { get; private set; }

    /// <summary>Партия закончилась сдачей, а не по правилам игры.</summary>
    public bool EndedByResign { get; private set; }

    /// <summary>Почему партия не состоялась или прервана (для состояния <see cref="GameState.Aborted"/>).</summary>
    public string AbortReason { get; set; } = "";

    /// <summary>Сколько ходов (в морском бое — выстрелов) сделано обоими.</summary>
    public int MoveCount { get; protected set; }

    public abstract GameKind Kind { get; }

    public string Title => TitleOf(Kind);

    public abstract bool IsMyTurn { get; }

    /// <summary>Кем мы играем — для подписи «Вы — …» («✕», «белыми»); пусто, если стороны не различаются.</summary>
    public abstract string MySide { get; }

    public abstract string OpponentSide { get; }

    /// <summary>Подсказка в приглашении: кем будет играть приглашённый.</summary>
    public abstract string InviteHint { get; }

    /// <summary>Что дописать к итогу в истории переписки: доска, счёт.</summary>
    public abstract string SummaryText();

    /// <summary>Что-то изменилось — панель с партией должна перерисоваться.</summary>
    public event Action? Changed;

    public void SetState(GameState state)
    {
        State = state;
        RaiseChanged();
    }

    public void RaiseChanged() => Changed?.Invoke();

    public void Resign(bool byMe)
    {
        if (State != GameState.Playing) return;
        EndedByResign = true;
        Finish(byMe ? GameResult.OpponentWon : GameResult.IWon);
    }

    protected void Finish(GameResult result)
    {
        Result = result;
        State = GameState.Finished;
        RaiseChanged();
    }

    public static string TitleOf(GameKind kind) => kind switch
    {
        GameKind.Checkers => "Шашки",
        GameKind.Battleship => "Морской бой",
        _ => "Крестики-нолики",
    };

    /// <summary>Код игры в пакетах. У старых версий его нет — значит, крестики-нолики.</summary>
    public static string CodeOf(GameKind kind) => kind switch
    {
        GameKind.Checkers => "checkers",
        GameKind.Battleship => "battleship",
        _ => "",
    };

    public static GameKind? KindOf(string code) => code switch
    {
        "" or "tictactoe" => GameKind.TicTacToe,
        "checkers" => GameKind.Checkers,
        "battleship" => GameKind.Battleship,
        _ => null,
    };

    public static BoardGame Create(GameKind kind, Guid id, Contact opponent, bool iAmFirst, GameState state) => kind switch
    {
        GameKind.Checkers => new CheckersGame { Id = id, Opponent = opponent, IAmFirst = iAmFirst, State = state },
        GameKind.Battleship => new BattleshipGame { Id = id, Opponent = opponent, IAmFirst = iAmFirst, State = state },
        _ => new TicTacToeGame { Id = id, Opponent = opponent, IAmFirst = iAmFirst, State = state },
    };
}
