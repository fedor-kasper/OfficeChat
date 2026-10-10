using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OfficeChat.Models;

/// <summary>Как часто повторять напоминание после «Готово».</summary>
public enum ReminderRepeat
{
    None,
    Daily,
    Weekdays,
    Weekly,
    Monthly,
}

/// <summary>Участник общего напоминания и отметил ли он его выполненным.</summary>
public sealed record ReminderParticipant(Guid Id, string Name, bool Done);

/// <summary>
/// Напоминание на доске. Важность 1…5 задаёт цвет и поведение:
/// 1 — тихое уведомление, 2 — обычное, 3 — красная рамка и повтор каждые 15 минут,
/// 4 — повтор каждые 5 минут и мигающий значок, 5 — повтор каждые 2 минуты, окно не закрыть без ответа.
/// Общее напоминание (с участниками) есть у каждого участника; кто выполнил — видно всем.
/// </summary>
public sealed class Reminder : INotifyPropertyChanged
{
    public const int MinImportance = 1;
    public const int MaxImportance = 5;

    private string _text = "";
    private DateTime? _dueAt;
    private int _importance = 2;
    private ReminderRepeat _repeat;
    private bool _isDone;
    private DateTime? _doneAt;
    private DateTime? _snoozedUntil;
    private DateTime? _nextAlertAt;
    private bool _isAlerting;
    private List<ReminderParticipant> _participants = new();

    public required Guid Id { get; init; }

    public string Text
    {
        get => _text;
        set => Set(ref _text, value);
    }

    /// <summary>Когда напомнить; null — просто запись на доске без уведомления.</summary>
    public DateTime? DueAt
    {
        get => _dueAt;
        set
        {
            if (Set(ref _dueAt, value)) OnTimeChanged();
        }
    }

    /// <summary>Важность 1…5.</summary>
    public int Importance
    {
        get => _importance;
        set
        {
            if (!Set(ref _importance, Math.Clamp(value, MinImportance, MaxImportance))) return;
            OnPropertyChanged(nameof(ImportanceName));
            OnPropertyChanged(nameof(Color));
            OnPropertyChanged(nameof(Tint));
            OnPropertyChanged(nameof(CanClosePopup));
        }
    }

    public ReminderRepeat Repeat
    {
        get => _repeat;
        set
        {
            if (Set(ref _repeat, value)) OnPropertyChanged(nameof(RepeatText));
        }
    }

    public bool IsDone
    {
        get => _isDone;
        set
        {
            if (!Set(ref _isDone, value)) return;
            OnPropertyChanged(nameof(IsActive));
            OnTimeChanged();
        }
    }

    /// <summary>Ещё не выполнено.</summary>
    public bool IsActive => !IsDone;

    public DateTime? DoneAt
    {
        get => _doneAt;
        set => Set(ref _doneAt, value);
    }

    /// <summary>Отложено до этого времени (вместо <see cref="DueAt"/>).</summary>
    public DateTime? SnoozedUntil
    {
        get => _snoozedUntil;
        set
        {
            if (Set(ref _snoozedUntil, value)) OnTimeChanged();
        }
    }

    /// <summary>Когда показать уведомление в следующий раз (null — не показывать).</summary>
    public DateTime? NextAlertAt
    {
        get => _nextAlertAt;
        set => Set(ref _nextAlertAt, value);
    }

    /// <summary>Напоминание сработало и ждёт ответа («Готово» или «Отложить»).</summary>
    public bool IsAlerting
    {
        get => _isAlerting;
        set
        {
            if (Set(ref _isAlerting, value)) OnTimeChanged();
        }
    }

    public Guid CreatorId { get; init; }
    public string CreatorName { get; init; } = "";
    public DateTime CreatedAt { get; init; }

    /// <summary>Кому ещё отправлено (без автора).</summary>
    public List<ReminderParticipant> Participants
    {
        get => _participants;
        set
        {
            _participants = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ParticipantsText));
            OnPropertyChanged(nameof(HasParticipants));
        }
    }

    // ---- Для показа ----

    /// <summary>Создано на этом компьютере (иначе — прислал коллега).</summary>
    public bool IsMine { get; set; } = true;

    public bool IsFromOther => !IsMine;

    public string FromText => IsMine ? "" : $"автор: {CreatorName}";

    public bool HasParticipants => Participants.Count > 0;

    public string ParticipantsText => string.Join(", ", Participants.Select(p => p.Done ? $"✓ {p.Name}" : p.Name));

    /// <summary>Когда сработает: отложенное время или срок.</summary>
    public DateTime? EffectiveTime => SnoozedUntil ?? DueAt;

    public bool IsOverdue => !IsDone && EffectiveTime is { } time && time <= DateTime.Now;

    public bool HasTime => EffectiveTime != null;

    public string DueText
    {
        get
        {
            if (EffectiveTime is not { } time) return "без срока";
            var text = FormatTime(time);
            if (SnoozedUntil != null) text = $"отложено до {text}";
            if (IsOverdue) text += " — просрочено";
            return text;
        }
    }

    public string RepeatText => Repeat switch
    {
        ReminderRepeat.Daily => "🔁 каждый день",
        ReminderRepeat.Weekdays => "🔁 по будням",
        ReminderRepeat.Weekly => "🔁 каждую неделю",
        ReminderRepeat.Monthly => "🔁 каждый месяц",
        _ => "",
    };

    public bool HasRepeat => Repeat != ReminderRepeat.None;

    public string ImportanceName => NameOf(Importance);

    public string Color => ColorOf(Importance);

    /// <summary>Светлый фон карточки в цвет важности.</summary>
    public string Tint => TintOf(Importance);

    /// <summary>Критическое уведомление нельзя просто закрыть — только «Готово» или «Отложить».</summary>
    public bool CanClosePopup => Importance < MaxImportance;

    public static string NameOf(int importance) => importance switch
    {
        1 => "Низкая",
        2 => "Обычная",
        3 => "Важная",
        4 => "Срочная",
        _ => "Критическая",
    };

    public static string ColorOf(int importance) => importance switch
    {
        1 => "#9CA3AF",
        2 => "#3B82F6",
        3 => "#F59E0B",
        4 => "#EF4444",
        _ => "#991B1B",
    };

    public static string TintOf(int importance) => importance switch
    {
        1 => "#F9FAFB",
        2 => "#EFF6FF",
        3 => "#FFFBEB",
        4 => "#FEF2F2",
        _ => "#FEE2E2",
    };

    /// <summary>Что делает напоминание этой важности — подсказка под ползунком.</summary>
    public static string BehaviorOf(int importance) => importance switch
    {
        1 => "Тихое уведомление маленькой полоской, один раз.",
        2 => "Обычное уведомление, один раз.",
        3 => "Уведомление с красной рамкой; повторяется каждые 15 минут, пока не отметите «Готово» или не отложите.",
        4 => "Повторяется каждые 5 минут, значок в трее мигает.",
        _ => "Повторяется каждые 2 минуты; окно нельзя просто закрыть — только «Готово» или «Отложить».",
    };

    /// <summary>Через сколько повторить уведомление, если на него не ответили (null — не повторять).</summary>
    public static TimeSpan? NagIntervalOf(int importance) => importance switch
    {
        3 => TimeSpan.FromMinutes(15),
        4 => TimeSpan.FromMinutes(5),
        5 => TimeSpan.FromMinutes(2),
        _ => null,
    };

    public static string FormatTime(DateTime time)
    {
        var today = DateTime.Today;
        if (time.Date == today) return $"сегодня {time:HH:mm}";
        if (time.Date == today.AddDays(1)) return $"завтра {time:HH:mm}";
        if (time.Date == today.AddDays(-1)) return $"вчера {time:HH:mm}";
        return time.Year == today.Year ? time.ToString("dd.MM HH:mm") : time.ToString("dd.MM.yyyy HH:mm");
    }

    /// <summary>Время суток из того, как его обычно вводят: «9:00», «9.30», «0930», «14».</summary>
    public static TimeSpan? ParseTimeOfDay(string? text)
    {
        text = (text ?? "").Trim().Replace('.', ':').Replace('-', ':');
        if (text.Length == 4 && text.All(char.IsDigit)) text = text[..2] + ":" + text[2..];
        if (!text.Contains(':') && int.TryParse(text, out var hours) && hours is >= 0 and < 24)
            return TimeSpan.FromHours(hours);
        return TimeSpan.TryParseExact(text, new[] { @"h\:mm", @"hh\:mm" }, System.Globalization.CultureInfo.InvariantCulture,
                   out var time) && time < TimeSpan.FromDays(1)
            ? time
            : null;
    }

    /// <summary>Следующий срок для повторяющегося напоминания (после <paramref name="after"/>).</summary>
    public DateTime? NextOccurrence(DateTime after)
    {
        if (DueAt is not { } due || Repeat == ReminderRepeat.None) return null;
        var next = due;
        do
        {
            next = Repeat switch
            {
                ReminderRepeat.Daily => next.AddDays(1),
                ReminderRepeat.Weekly => next.AddDays(7),
                ReminderRepeat.Monthly => next.AddMonths(1),
                _ => next.AddDays(next.DayOfWeek switch
                {
                    DayOfWeek.Friday => 3,
                    DayOfWeek.Saturday => 2,
                    _ => 1,
                }),
            };
        }
        while (next <= after);
        return next;
    }

    /// <summary>Пересчитать подписи, зависящие от текущего времени (вызывается раз в минуту).</summary>
    public void RefreshTimeTexts() => OnTimeChanged();

    private void OnTimeChanged()
    {
        OnPropertyChanged(nameof(EffectiveTime));
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(HasTime));
        OnPropertyChanged(nameof(DueText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RaiseParticipantsChanged()
    {
        OnPropertyChanged(nameof(Participants));
        OnPropertyChanged(nameof(ParticipantsText));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
