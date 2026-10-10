using System.Windows.Threading;
using OfficeChat.Models;

namespace OfficeChat.Services;

/// <summary>
/// Мини-игры по сети (крестики-нолики, шашки, морской бой): приглашения, ходы по очереди, сдача,
/// итог в историю переписки. Пакеты игры уходят по порядку через очередь и повторяются, пока не дойдут,
/// поэтому кратковременный обрыв связи не ломает партию. Работает в UI-потоке.
/// </summary>
public sealed class GameService : IDisposable
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    private readonly ChatService _chat;

    // Незаконченные партии по номеру партии.
    private readonly Dictionary<Guid, BoardGame> _games = new();

    // Партия, которую показывать в переписке с собеседником (по его постоянному Id),
    // в том числе законченная — пока её не закроют кнопкой «Закрыть».
    private readonly Dictionary<Guid, BoardGame> _shown = new();

    // Закрытые партии: повтор старого пакета (если не дошло подтверждение) не должен их воскресить.
    private readonly HashSet<Guid> _closedGameIds = new();

    private readonly List<(Guid PeerId, ChatPacket Packet)> _outbox = new();
    private readonly HashSet<Guid> _flushing = new();
    private readonly DispatcherTimer _retryTimer;

    /// <summary>Нас пригласили — нужно показать «Принять / Отклонить».</summary>
    public event Action<BoardGame>? InviteReceived;

    /// <summary>Соперник сходил — теперь наш ход.</summary>
    public event Action<BoardGame>? OpponentMoved;

    /// <summary>У собеседника появилась, изменилась или убрана партия — панель в переписке обновляется.</summary>
    public event Action<Contact>? GameChanged;

    public GameService(ChatService chat)
    {
        _chat = chat;
        _chat.GamePacketReceived += OnPacket;
        _chat.PresenceChanged += FlushAll;

        _retryTimer = new DispatcherTimer { Interval = RetryInterval };
        _retryTimer.Tick += (_, _) => FlushAll();
        _retryTimer.Start();
    }

    /// <summary>Партия для показа в переписке с этим собеседником (идущая или только что законченная).</summary>
    public BoardGame? GameFor(Contact contact) =>
        contact.Peer is { } peer ? _shown.GetValueOrDefault(peer.Id) : null;

    /// <summary>Незаконченная партия (или приглашение) с этим собеседником.</summary>
    public BoardGame? ActiveGameWith(Contact contact) =>
        GameFor(contact) is { State: GameState.Inviting or GameState.Invited or GameState.Playing } game ? game : null;

    // ---- Действия игрока ----

    public BoardGame Invite(Contact contact, GameKind kind)
    {
        if (ActiveGameWith(contact) is { } existing) return existing;

        var game = BoardGame.Create(kind, Guid.NewGuid(), contact, iAmFirst: true, GameState.Inviting);
        Register(game);
        Send(game, ChatPacket.GameInvite, p => p.GameKind = BoardGame.CodeOf(kind));
        return game;
    }

    public void Accept(BoardGame game)
    {
        if (game.State != GameState.Invited) return;
        // Сначала ставим ответ в очередь, потом меняем состояние: обработчики изменений не должны его потерять.
        Send(game, ChatPacket.GameAccept);
        game.SetState(GameState.Playing);
        RaiseChanged(game);
    }

    public void Decline(BoardGame game)
    {
        if (game.State != GameState.Invited) return;
        Send(game, ChatPacket.GameDecline);
        game.SetState(GameState.Aborted);
        _chat.AddGameRecord(game.Opponent, $"🎮 {game.Title}: вы отклонили приглашение");
        Close(game);
        Dismiss(game);
    }

    /// <summary>Пригласивший передумал, пока ему не ответили.</summary>
    public void CancelInvite(BoardGame game)
    {
        if (game.State != GameState.Inviting) return;
        Send(game, ChatPacket.GameCancel);
        game.SetState(GameState.Aborted);
        Close(game);
        Dismiss(game);
    }

    /// <summary>Крестики-нолики: поставить знак в клетку.</summary>
    public bool Move(TicTacToeGame game, int cell)
    {
        if (!game.CanPlay(cell)) return false;

        var moveNumber = game.MoveCount;
        Send(game, ChatPacket.GameMove, p =>
        {
            p.Cell = cell;
            p.MoveNumber = moveNumber;
        });
        game.ApplyMove(cell, moveNumber, byMe: true);
        RecordIfFinished(game);
        return true;
    }

    /// <summary>Шашки: сходить шашкой по пути (начальная клетка, затем каждая остановка).</summary>
    public bool Move(CheckersGame game, IReadOnlyList<int> path)
    {
        if (!game.CanPlay(path)) return false;

        var moveNumber = game.MoveCount;
        Send(game, ChatPacket.GameMove, p =>
        {
            p.Cells = path.ToList();
            p.MoveNumber = moveNumber;
        });
        game.ApplyMove(path, moveNumber, byMe: true);
        RecordIfFinished(game);
        return true;
    }

    /// <summary>Морской бой: флот расставлен.</summary>
    public void Ready(BattleshipGame game)
    {
        if (game.IAmReady || game.State != GameState.Playing) return;
        Send(game, ChatPacket.GameReady);
        game.SetReady();
    }

    /// <summary>Морской бой: выстрел. Результат придёт ответом от соперника.</summary>
    public bool Shoot(BattleshipGame game, int cell)
    {
        if (!game.CanShoot(cell)) return false;

        var shotNumber = game.MoveCount;
        Send(game, ChatPacket.GameMove, p =>
        {
            p.Cell = cell;
            p.MoveNumber = shotNumber;
        });
        game.BeginShot(cell);
        return true;
    }

    public void Resign(BoardGame game)
    {
        if (game.State != GameState.Playing) return;
        Send(game, ChatPacket.GameResign);
        game.Resign(byMe: true);
        RecordIfFinished(game);
    }

    /// <summary>
    /// Прервать партию без результата — когда соперник вышел из сети и неизвестно, вернётся ли.
    /// Если он вернётся, его ходы в эту партию будут проигнорированы.
    /// </summary>
    public void Abandon(BoardGame game)
    {
        if (game.State != GameState.Playing) return;
        game.AbortReason = "Партия прервана";
        game.SetState(GameState.Aborted);
        _chat.AddGameRecord(game.Opponent,
            $"🎮 {game.Title}: партия прервана — {game.Opponent.Title} вышел(-ла) из сети\n{game.SummaryText()}");
        Close(game);
        RaiseChanged(game);
    }

    /// <summary>Убрать законченную партию из переписки (кнопка «Закрыть»).</summary>
    public void Dismiss(BoardGame game)
    {
        var peerId = game.Opponent.Peer!.Id;
        if (_shown.TryGetValue(peerId, out var shown) && shown == game)
            _shown.Remove(peerId);
        _chat.UnpinContact(game.Opponent);
        RaiseChanged(game);
    }

    /// <summary>Выход из программы: незаконченные партии сдаём, приглашения отменяем.</summary>
    public void LeaveAll()
    {
        foreach (var game in _games.Values.ToList())
        {
            if (game.State == GameState.Playing) Resign(game);
            else if (game.State == GameState.Inviting) CancelInvite(game);
            else if (game.State == GameState.Invited) Decline(game);
        }
    }

    // ---- Пакеты от соперника ----

    private void OnPacket(Contact contact, ChatPacket packet)
    {
        _games.TryGetValue(packet.GameId, out var game);
        // Сравниваем по постоянному Id: объект контакта мог пересоздаться, а человек тот же.
        if (game != null && game.Opponent.Peer!.Id != contact.Peer!.Id) return;

        switch (packet.Type)
        {
            case ChatPacket.GameInvite when game == null && !_closedGameIds.Contains(packet.GameId):
                OnInvite(contact, packet);
                break;

            case ChatPacket.GameAccept when game?.State == GameState.Inviting:
                game.SetState(GameState.Playing);
                RaiseChanged(game);
                break;

            case ChatPacket.GameDecline when game?.State == GameState.Inviting:
                game.AbortReason = $"{contact.Title} отклонил(а) приглашение";
                game.SetState(GameState.Aborted);
                _chat.AddGameRecord(contact, $"🎮 {game.Title}: {contact.Title} отклонил(а) приглашение");
                Close(game);
                RaiseChanged(game);
                break;

            case ChatPacket.GameCancel when game?.State == GameState.Invited:
                game.SetState(GameState.Aborted);
                Close(game);
                Dismiss(game);
                break;

            case ChatPacket.GameMove when game != null:
                OnOpponentMove(game, packet);
                break;

            case ChatPacket.GameReady when game is BattleshipGame battleship:
                battleship.SetOpponentReady();
                if (battleship.IsMyTurn) OpponentMoved?.Invoke(battleship);
                break;

            case ChatPacket.GameShotResult when game is BattleshipGame battleship:
                if (battleship.ApplyShotResult(packet.Cell, packet.MoveNumber, (ShotOutcome)packet.ShotResult, packet.Cells))
                    RecordIfFinished(battleship);
                break;

            case ChatPacket.GameResign when game?.State == GameState.Playing:
                game.Resign(byMe: false);
                RecordIfFinished(game);
                break;
        }
    }

    private void OnOpponentMove(BoardGame game, ChatPacket packet)
    {
        var applied = false;
        switch (game)
        {
            case TicTacToeGame ticTacToe:
                applied = ticTacToe.ApplyMove(packet.Cell, packet.MoveNumber, byMe: false);
                break;
            case CheckersGame checkers when packet.Cells != null:
                applied = checkers.ApplyMove(packet.Cells, packet.MoveNumber, byMe: false);
                break;
            case BattleshipGame battleship:
                // Соперник выстрелил по нашему полю — отвечаем, попал ли он.
                if (battleship.ApplyOpponentShot(packet.Cell, packet.MoveNumber) is { } answer)
                {
                    Send(game, ChatPacket.GameShotResult, p =>
                    {
                        p.Cell = packet.Cell;
                        p.MoveNumber = packet.MoveNumber;
                        p.ShotResult = (int)answer.Outcome;
                        p.Cells = answer.SunkShip?.ToList();
                    });
                    applied = true;
                }
                break;
        }
        if (!applied) return;
        RecordIfFinished(game);
        if (game.IsMyTurn)
            OpponentMoved?.Invoke(game);
    }

    private void OnInvite(Contact contact, ChatPacket packet)
    {
        var kind = BoardGame.KindOf(packet.GameKind);
        // С этим человеком уже есть партия или встречное приглашение — второе не нужно.
        // Незнакомая игра (из более новой версии программы) — тоже отказываемся.
        if (ActiveGameWith(contact) != null || kind == null)
        {
            _closedGameIds.Add(packet.GameId);
            _outbox.Add((contact.Peer!.Id, new ChatPacket
            {
                Type = ChatPacket.GameDecline,
                Id = Guid.NewGuid(),
                GameId = packet.GameId,
            }));
            _ = FlushAsync(contact.Peer.Id);
            return;
        }

        var game = BoardGame.Create(kind.Value, packet.GameId, contact, iAmFirst: false, GameState.Invited);
        Register(game);
        InviteReceived?.Invoke(game);
    }

    /// <summary>Итог партии — в историю переписки (у каждого своя формулировка).</summary>
    private void RecordIfFinished(BoardGame game)
    {
        if (game.State != GameState.Finished || !_games.ContainsKey(game.Id)) return;
        Close(game);

        var name = game.Opponent.Title;
        var outcome = game.Result switch
        {
            GameResult.IWon when game.EndedByResign => $"{name} сдался(-ась) — вы победили! 🏆",
            GameResult.IWon => "вы победили! 🏆",
            GameResult.OpponentWon when game.EndedByResign => $"вы сдались — победил(а) {name}",
            GameResult.OpponentWon => $"победил(а) {name}",
            _ => "ничья 🤝",
        };
        var side = game.MySide.Length > 0 ? $" (вы — {game.MySide})" : "";
        _chat.AddGameRecord(game.Opponent, $"🎮 {game.Title}{side}: {outcome}\n{game.SummaryText()}");
        RaiseChanged(game);
    }

    private void Register(BoardGame game)
    {
        _games.Add(game.Id, game);
        _shown[game.Opponent.Peer!.Id] = game;
        // Пока идёт игра, собеседник не должен пропадать из списка.
        _chat.PinContact(game.Opponent);
        RaiseChanged(game);
    }

    private void Close(BoardGame game)
    {
        _games.Remove(game.Id);
        _closedGameIds.Add(game.Id);
    }

    private void RaiseChanged(BoardGame game) => GameChanged?.Invoke(game.Opponent);

    // ---- Доставка ----

    private void Send(BoardGame game, string type, Action<ChatPacket>? fill = null)
    {
        var peerId = game.Opponent.Peer!.Id;
        var packet = new ChatPacket
        {
            Type = type,
            Id = Guid.NewGuid(),
            GameId = game.Id,
        };
        fill?.Invoke(packet);
        _outbox.Add((peerId, packet));
        _ = FlushAsync(peerId);
    }

    private void FlushAll()
    {
        foreach (var peerId in _outbox.Select(o => o.PeerId).Distinct().ToList())
            _ = FlushAsync(peerId);
    }

    /// <summary>Отправляет пакеты собеседнику строго по порядку; при ошибке ждёт следующей попытки.</summary>
    private async Task FlushAsync(Guid peerId)
    {
        if (!_flushing.Add(peerId)) return;
        try
        {
            while (_outbox.FirstOrDefault(o => o.PeerId == peerId) is { Packet: not null } item)
            {
                // Берём актуальный контакт: старый объект мог устареть, а адрес — смениться.
                var contact = _chat.FindContact(peerId);
                if (contact == null || !await _chat.SendPacketAsync(contact, item.Packet)) return;
                _outbox.Remove(item);
            }
        }
        finally
        {
            _flushing.Remove(peerId);
        }
    }

    public void Dispose()
    {
        _retryTimer.Stop();
        _chat.GamePacketReceived -= OnPacket;
        _chat.PresenceChanged -= FlushAll;
    }
}
