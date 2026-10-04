using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OfficeChat.Models;

/// <summary>Строка в списке слева: либо конкретный компьютер со своей перепиской, либо «Все».</summary>
public sealed class Contact : INotifyPropertyChanged
{
    private int _unreadCount;

    /// <summary>Компьютер собеседника; null — рассылка всем.</summary>
    public Peer? Peer { get; }

    public bool IsEveryone => Peer == null;

    /// <summary>Личная переписка (у «Все» всегда пуста — такие сообщения раскладываются по личным).</summary>
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public string Title => Peer?.Name ?? "Все";

    public bool IsOnline => Peer?.IsOnline ?? true;

    public string Subtitle => Peer == null
        ? "Отправить каждому, кто в сети"
        : Peer.IsOnline ? $"{Peer.Machine} · {Peer.Address}" : "не в сети";

    public int UnreadCount
    {
        get => _unreadCount;
        set
        {
            if (_unreadCount == value) return;
            _unreadCount = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUnread));
        }
    }

    public bool HasUnread => UnreadCount > 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private Contact(Peer? peer)
    {
        Peer = peer;
        if (peer != null)
            peer.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(IsOnline));
            };
    }

    public static Contact Everyone() => new(null);

    public static Contact For(Peer peer) => new(peer);

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
