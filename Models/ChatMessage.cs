using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OfficeChat.Models;

public enum MessageStatus
{
    /// <summary>Ждёт отправки: адресат не в сети или отправка не удалась.</summary>
    Queued,
    Sending,
    Delivered,
    Read,
}

public enum MessageKind
{
    Text = 0,
    /// <summary>Итог мини-игры — служебная запись в истории, по сети не передаётся.</summary>
    Game = 1,
}

/// <summary>Одно сообщение в личной переписке (входящее или исходящее).</summary>
public sealed class ChatMessage : INotifyPropertyChanged
{
    private MessageStatus _status;
    private bool _peerOffline;
    private bool _isRead;

    public required Guid Id { get; init; }
    public required bool IsOutgoing { get; init; }
    public required string Text { get; init; }
    public required DateTime Timestamp { get; init; }

    public MessageKind Kind { get; init; }

    public bool IsGame => Kind == MessageKind.Game;

    /// <summary>Сообщение было отправлено «Всем», а не лично.</summary>
    public bool IsBroadcast { get; init; }

    // ---- Исходящие ----

    public MessageStatus Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(CanCancel));
        }
    }

    /// <summary>Для сообщения в очереди: true — адресат не в сети, false — сеть есть, но отправка не удалась.</summary>
    public bool PeerOffline
    {
        get => _peerOffline;
        set
        {
            if (Set(ref _peerOffline, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>Отменить можно только то, что ещё не ушло адресату.</summary>
    public bool CanCancel => IsOutgoing && Status == MessageStatus.Queued;

    public string StatusText => !IsOutgoing ? "" : Status switch
    {
        MessageStatus.Queued => PeerOffline
            ? "⏳ Адресат не в сети — отправится, когда он появится"
            : "⏳ Не удалось отправить — повторяем…",
        MessageStatus.Sending => "Отправляется…",
        MessageStatus.Delivered => "✓ Доставлено",
        MessageStatus.Read => "✓✓ Прочитано",
        _ => "",
    };

    // ---- Входящие ----

    /// <summary>Мы прочитали входящее сообщение.</summary>
    public bool IsRead
    {
        get => _isRead;
        set => Set(ref _isRead, value);
    }

    /// <summary>Отправителю уже сообщили, что сообщение прочитано.</summary>
    public bool ReadReceiptSent { get; set; }

    public string TimeText => Timestamp.Date == DateTime.Today
        ? Timestamp.ToString("HH:mm")
        : Timestamp.ToString("dd.MM HH:mm");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name!);
        return true;
    }
}
