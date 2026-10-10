using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Threading;
using OfficeChat.Models;

namespace OfficeChat.Services;

/// <summary>
/// Доска напоминаний: хранение, срабатывание по времени, повтор уведомлений для важных,
/// откладывание, повторяющиеся напоминания и общие напоминания с участниками
/// (рассылаются через ту же надёжную очередь, что и правки сообщений). Работает в UI-потоке.
/// </summary>
public sealed class ReminderService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    private readonly ChatService _chat;
    private readonly DispatcherTimer _timer;
    private DateTime _lastMinute;

    public ObservableCollection<Reminder> Reminders { get; } = new();

    /// <summary>Пора напомнить — показать уведомление.</summary>
    public event Action<Reminder>? ReminderDue;

    /// <summary>Коллега прислал новое напоминание.</summary>
    public event Action<Reminder>? ReminderReceived;

    /// <summary>Что-то изменилось — доска и счётчики перерисовываются.</summary>
    public event Action? Changed;

    /// <summary>Напоминание удалено — убрать его уведомление.</summary>
    public event Action<Reminder>? ReminderRemoved;

    public ReminderService(ChatService chat)
    {
        _chat = chat;
        _chat.ReminderPacketReceived += OnPacket;
        foreach (var json in _chat.Store.LoadReminders())
        {
            if (FromJson(json) is { } reminder)
                Reminders.Add(reminder);
        }

        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _lastMinute = DateTime.Now;
    }

    /// <summary>Активные — по важности, затем по сроку (без срока — в конце).</summary>
    public List<Reminder> Active => Reminders.Where(r => !r.IsDone)
        .OrderByDescending(r => r.Importance)
        .ThenBy(r => r.EffectiveTime ?? DateTime.MaxValue)
        .ToList();

    /// <summary>Выполненные — сначала последние.</summary>
    public List<Reminder> Done => Reminders.Where(r => r.IsDone).OrderByDescending(r => r.DoneAt).ToList();

    /// <summary>Сколько напоминаний просрочено или сработало и ждёт ответа — для счётчика на кнопке доски.</summary>
    public int OverdueCount => Reminders.Count(r => !r.IsDone && (r.IsOverdue || r.IsAlerting));

    /// <summary>Срочные и критические, которые сработали и ждут ответа, — из-за них мигает значок в трее.</summary>
    public int AttentionCount => Reminders.Count(r => !r.IsDone && r.IsAlerting && r.Importance >= 4);

    /// <summary>Первый запуск таймера сразу: просроченное, пока программа была закрыта, сработает при старте.</summary>
    public void Start() => Tick();

    // ---- Действия пользователя ----

    public Reminder Create(string text, DateTime? dueAt, int importance, ReminderRepeat repeat, IEnumerable<Contact> people)
    {
        var reminder = new Reminder
        {
            Id = Guid.NewGuid(),
            Text = text,
            DueAt = dueAt,
            Importance = importance,
            Repeat = dueAt == null ? ReminderRepeat.None : repeat,
            CreatorId = _chat.MyId,
            CreatorName = _chat.MyName,
            CreatedAt = DateTime.Now,
            Participants = people.Where(p => p.IsPerson)
                .Select(p => new ReminderParticipant(p.Peer!.Id, p.Title, false)).ToList(),
        };
        Schedule(reminder);
        Reminders.Add(reminder);
        Save(reminder);
        Share(reminder);
        Log.Info($"Создано напоминание «{Short(text)}», важность {importance}, участников {reminder.Participants.Count}");
        Changed?.Invoke();
        return reminder;
    }

    /// <summary>Изменить своё напоминание (новые участники получат его, остальные — изменения).</summary>
    public void Update(Reminder reminder, string text, DateTime? dueAt, int importance, ReminderRepeat repeat,
        IEnumerable<Contact> people)
    {
        if (!reminder.IsMine) return;
        var timeChanged = reminder.DueAt != dueAt;
        reminder.Text = text;
        reminder.DueAt = dueAt;
        reminder.Importance = importance;
        reminder.Repeat = dueAt == null ? ReminderRepeat.None : repeat;
        var wanted = people.Where(p => p.IsPerson).ToList();
        reminder.Participants = wanted
            .Select(p => reminder.Participants.FirstOrDefault(x => x.Id == p.Peer!.Id)
                         ?? new ReminderParticipant(p.Peer!.Id, p.Title, false))
            .ToList();
        if (timeChanged)
        {
            reminder.SnoozedUntil = null;
            Schedule(reminder);
        }
        Save(reminder);
        Share(reminder);
        Changed?.Invoke();
    }

    /// <summary>
    /// «Готово». Повторяющееся переходит на следующий срок, остальное — в выполненные.
    /// Участники общего напоминания видят, кто его выполнил.
    /// </summary>
    public void Complete(Reminder reminder)
    {
        if (reminder.IsDone) return;
        if (reminder.NextOccurrence(DateTime.Now) is { } next)
        {
            reminder.DueAt = next;
            reminder.SnoozedUntil = null;
            Schedule(reminder);
        }
        else
        {
            reminder.IsDone = true;
            reminder.DoneAt = DateTime.Now;
            reminder.IsAlerting = false;
            reminder.NextAlertAt = null;
        }
        Save(reminder);
        SendDone(reminder, done: true);
        Changed?.Invoke();
    }

    /// <summary>Вернуть выполненное на доску.</summary>
    public void Reopen(Reminder reminder)
    {
        if (!reminder.IsDone) return;
        reminder.IsDone = false;
        reminder.DoneAt = null;
        reminder.SnoozedUntil = null;
        Schedule(reminder);
        Save(reminder);
        SendDone(reminder, done: false);
        Changed?.Invoke();
    }

    /// <summary>Отложить до указанного времени.</summary>
    public void Snooze(Reminder reminder, DateTime until)
    {
        if (reminder.IsDone) reminder.IsDone = false;
        reminder.SnoozedUntil = until;
        reminder.IsAlerting = false;
        reminder.NextAlertAt = until;
        Save(reminder);
        Changed?.Invoke();
    }

    /// <summary>
    /// Уведомление закрыли крестиком. Обычное и тихое на этом успокаиваются;
    /// важные напомнят снова (интервал уже назначен при срабатывании).
    /// </summary>
    public void Dismiss(Reminder reminder)
    {
        if (Reminder.NagIntervalOf(reminder.Importance) != null) return;
        reminder.IsAlerting = false;
        Save(reminder);
        Changed?.Invoke();
    }

    /// <summary>Удалить. Своё общее напоминание удаляется и у участников.</summary>
    public void Delete(Reminder reminder)
    {
        Reminders.Remove(reminder);
        _chat.Store.DeleteReminder(reminder.Id);
        if (reminder.IsMine)
        {
            foreach (var participant in reminder.Participants)
                _chat.SendReliable(participant.Id, new ChatPacket
                {
                    Type = ChatPacket.ReminderDelete, Id = Guid.NewGuid(), TargetId = reminder.Id,
                });
        }
        ReminderRemoved?.Invoke(reminder);
        Changed?.Invoke();
    }

    /// <summary>Готовые варианты «Отложить»: подпись и время.</summary>
    public static List<(string Title, DateTime Until)> SnoozeChoices()
    {
        var now = DateTime.Now;
        var tomorrowMorning = DateTime.Today.AddDays(1).AddHours(9);
        return new()
        {
            ("на 10 минут", now.AddMinutes(10)),
            ("на 30 минут", now.AddMinutes(30)),
            ("на 1 час", now.AddHours(1)),
            ("на 3 часа", now.AddHours(3)),
            ("до завтра, 9:00", tomorrowMorning),
        };
    }

    // ---- Время ----

    private static void Schedule(Reminder reminder)
    {
        reminder.IsAlerting = false;
        reminder.NextAlertAt = reminder.IsDone ? null : reminder.EffectiveTime;
    }

    private void Tick()
    {
        var now = DateTime.Now;
        var changed = false;
        foreach (var reminder in Reminders.Where(r => !r.IsDone && r.NextAlertAt is { } at && at <= now).ToList())
        {
            reminder.IsAlerting = true;
            reminder.NextAlertAt = Reminder.NagIntervalOf(reminder.Importance) is { } nag ? now + nag : null;
            Save(reminder);
            changed = true;
            Log.Info($"Напоминание «{Short(reminder.Text)}» (важность {reminder.Importance})");
            ReminderDue?.Invoke(reminder);
        }

        // Раз в минуту обновляем «сегодня / просрочено» на карточках.
        if (now.Minute != _lastMinute.Minute || now - _lastMinute > TimeSpan.FromMinutes(1))
        {
            _lastMinute = now;
            foreach (var reminder in Reminders) reminder.RefreshTimeTexts();
            changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    // ---- Общие напоминания ----

    /// <summary>Отправить напоминание участникам (новое или изменённое).</summary>
    private void Share(Reminder reminder)
    {
        if (!reminder.IsMine) return;
        var json = JsonSerializer.Serialize(ToShared(reminder));
        foreach (var participant in reminder.Participants)
            _chat.SendReliable(participant.Id, new ChatPacket { Type = ChatPacket.ReminderShare, Id = Guid.NewGuid(), Text = json });
    }

    /// <summary>Сообщить автору и остальным участникам, что мы выполнили (или снова открыли) напоминание.</summary>
    private void SendDone(Reminder reminder, bool done)
    {
        var recipients = reminder.Participants.Select(p => p.Id).Append(reminder.CreatorId)
            .Where(id => id != _chat.MyId).Distinct();
        foreach (var id in recipients)
            _chat.SendReliable(id, new ChatPacket
            {
                Type = ChatPacket.ReminderDone, Id = Guid.NewGuid(), TargetId = reminder.Id, Text = done ? "done" : "undone",
            });
        // У себя тоже отмечаем, если мы — участник чужого напоминания.
        MarkParticipant(reminder, _chat.MyId, done);
    }

    private void OnPacket(Contact contact, ChatPacket packet)
    {
        var from = contact.Peer!.Id;
        switch (packet.Type)
        {
            case ChatPacket.ReminderShare:
                OnShared(packet.Text, from);
                break;
            case ChatPacket.ReminderDelete:
                if (Reminders.FirstOrDefault(r => r.Id == packet.TargetId) is { IsMine: false } removed &&
                    removed.CreatorId == from)
                {
                    Reminders.Remove(removed);
                    _chat.Store.DeleteReminder(removed.Id);
                    ReminderRemoved?.Invoke(removed);
                    Changed?.Invoke();
                }
                break;
            case ChatPacket.ReminderDone:
                if (Reminders.FirstOrDefault(r => r.Id == packet.TargetId) is { } target)
                {
                    MarkParticipant(target, from, packet.Text == "done");
                    Save(target);
                    Changed?.Invoke();
                }
                break;
        }
    }

    private void OnShared(string json, Guid from)
    {
        SharedReminder? shared;
        try
        {
            shared = JsonSerializer.Deserialize<SharedReminder>(json);
        }
        catch (JsonException)
        {
            return;
        }
        // Изменять напоминание может только его автор.
        if (shared == null || shared.CreatorId != from) return;

        var existing = Reminders.FirstOrDefault(r => r.Id == shared.Id);
        if (existing != null)
        {
            var due = shared.DueAt?.LocalDateTime;
            var timeChanged = existing.DueAt != due;
            existing.Text = shared.Text;
            existing.DueAt = due;
            existing.Importance = shared.Importance;
            existing.Repeat = shared.Repeat;
            existing.Participants = shared.Participants;
            if (timeChanged)
            {
                existing.SnoozedUntil = null;
                Schedule(existing);
            }
            Save(existing);
            Changed?.Invoke();
            return;
        }

        var reminder = new Reminder
        {
            Id = shared.Id,
            Text = shared.Text,
            DueAt = shared.DueAt?.LocalDateTime,
            Importance = shared.Importance,
            Repeat = shared.Repeat,
            CreatorId = shared.CreatorId,
            CreatorName = shared.CreatorName,
            CreatedAt = DateTime.Now,
            Participants = shared.Participants,
            IsMine = false,
        };
        Schedule(reminder);
        Reminders.Add(reminder);
        Save(reminder);
        Log.Info($"«{shared.CreatorName}» прислал(а) напоминание «{Short(shared.Text)}»");
        ReminderReceived?.Invoke(reminder);
        Changed?.Invoke();
    }

    private static void MarkParticipant(Reminder reminder, Guid id, bool done)
    {
        var index = reminder.Participants.FindIndex(p => p.Id == id);
        if (index < 0 || reminder.Participants[index].Done == done) return;
        reminder.Participants[index] = reminder.Participants[index] with { Done = done };
        reminder.RaiseParticipantsChanged();
    }

    // ---- Хранение ----

    private void Save(Reminder reminder)
    {
        if (Reminders.Contains(reminder))
            _chat.Store.SaveReminder(reminder.Id, JsonSerializer.Serialize(ToStored(reminder)));
    }

    private static string Short(string text) => text.Length > 40 ? text[..40] + "…" : text;

    private Reminder? FromJson(string json)
    {
        try
        {
            if (JsonSerializer.Deserialize<StoredReminder>(json) is not { } s) return null;
            return new Reminder
            {
                Id = s.Id,
                Text = s.Text,
                DueAt = s.DueAt,
                Importance = s.Importance,
                Repeat = s.Repeat,
                IsDone = s.IsDone,
                DoneAt = s.DoneAt,
                SnoozedUntil = s.SnoozedUntil,
                NextAlertAt = s.NextAlertAt,
                IsAlerting = s.IsAlerting,
                CreatorId = s.CreatorId,
                CreatorName = s.CreatorName,
                CreatedAt = s.CreatedAt,
                Participants = s.Participants ?? new(),
                IsMine = s.CreatorId == _chat.MyId,
            };
        }
        catch (JsonException ex)
        {
            Log.Warn("Повреждённое напоминание в базе — пропускаем", ex);
            return null;
        }
    }

    private static StoredReminder ToStored(Reminder r) => new()
    {
        Id = r.Id, Text = r.Text, DueAt = r.DueAt, Importance = r.Importance, Repeat = r.Repeat, IsDone = r.IsDone,
        DoneAt = r.DoneAt, SnoozedUntil = r.SnoozedUntil, NextAlertAt = r.NextAlertAt, IsAlerting = r.IsAlerting,
        CreatorId = r.CreatorId, CreatorName = r.CreatorName, CreatedAt = r.CreatedAt, Participants = r.Participants,
    };

    private static SharedReminder ToShared(Reminder r) => new()
    {
        Id = r.Id, Text = r.Text, DueAt = r.DueAt is { } due ? new DateTimeOffset(due) : null, Importance = r.Importance,
        Repeat = r.Repeat, CreatorId = r.CreatorId, CreatorName = r.CreatorName, Participants = r.Participants,
    };

    private sealed class StoredReminder
    {
        public Guid Id { get; set; }
        public string Text { get; set; } = "";
        public DateTime? DueAt { get; set; }
        public int Importance { get; set; } = 2;
        public ReminderRepeat Repeat { get; set; }
        public bool IsDone { get; set; }
        public DateTime? DoneAt { get; set; }
        public DateTime? SnoozedUntil { get; set; }
        public DateTime? NextAlertAt { get; set; }
        public bool IsAlerting { get; set; }
        public Guid CreatorId { get; set; }
        public string CreatorName { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public List<ReminderParticipant>? Participants { get; set; }
    }

    /// <summary>Что получает участник: без личного состояния автора (отложено, выполнено).</summary>
    private sealed class SharedReminder
    {
        public Guid Id { get; set; }
        public string Text { get; set; } = "";
        public DateTimeOffset? DueAt { get; set; }
        public int Importance { get; set; } = 2;
        public ReminderRepeat Repeat { get; set; }
        public Guid CreatorId { get; set; }
        public string CreatorName { get; set; } = "";
        public List<ReminderParticipant> Participants { get; set; } = new();
    }

    public void Dispose()
    {
        _timer.Stop();
        _chat.ReminderPacketReceived -= OnPacket;
    }
}
