using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OfficeChat.Models;

/// <summary>Строка в списке слева: конкретный компьютер со своей перепиской, группа или «Все».</summary>
public sealed class Contact : INotifyPropertyChanged
{
    private int _unreadCount;
    private bool _hasOlderMessages;

    /// <summary>Компьютер собеседника; null — группа или рассылка всем.</summary>
    public Peer? Peer { get; }

    /// <summary>Групповой чат; null — личная переписка или «Все».</summary>
    public ChatGroup? Group { get; }

    public bool IsEveryone => Peer == null && Group == null;

    public bool IsGroup => Group != null;

    /// <summary>Личная переписка с одним человеком.</summary>
    public bool IsPerson => Peer != null;

    /// <summary>Настоящая переписка (человек или группа), а не «Все».</summary>
    public bool IsConversation => !IsEveryone;

    /// <summary>Под каким Id переписка хранится в истории: Id собеседника или группы.</summary>
    public Guid Key => Peer?.Id ?? Group?.Id ?? Guid.Empty;

    /// <summary>Переписка (у «Все» всегда пуста — такие сообщения раскладываются по личным).</summary>
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public string Title => Peer?.Name ?? Group?.Name ?? "Все";

    /// <summary>Группа и «Все» всегда «в сети»: писать туда можно в любой момент.</summary>
    public bool IsOnline => Peer?.IsOnline ?? true;

    /// <summary>Позвать играть можно только человека в сети.</summary>
    public bool CanInviteToGame => Peer is { IsOnline: true };

    /// <summary>Вручную убрать из списка можно только того, кто не в сети (из группы — выйти).</summary>
    public bool CanRemove => Peer is { IsOnline: false };

    public string Subtitle => IsTyping ? TypingText
        : Group != null ? Group.StatusText
        : Peer == null ? "Отправить каждому, кто в сети"
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

    /// <summary>Уведомления выключены: сообщения приходят без всплывающих окон и не заставляют мигать значок.</summary>
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value) return;
            _isMuted = value;
            OnPropertyChanged();
        }
    }
    private bool _isMuted;

    /// <summary>«печатает…» (в группе — с именем); пусто, если никто не печатает.</summary>
    public string TypingText
    {
        get => _typingText;
        set
        {
            if (_typingText == value) return;
            _typingText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsTyping));
            OnPropertyChanged(nameof(Subtitle));
        }
    }
    private string _typingText = "";

    public bool IsTyping => _typingText.Length > 0;

    /// <summary>До какого момента показывать «печатает…» (если не придёт новое уведомление).</summary>
    public DateTime TypingUntil { get; set; }

    /// <summary>В истории есть сообщения раньше загруженных в окно.</summary>
    public bool HasOlderMessages
    {
        get => _hasOlderMessages;
        set
        {
            if (_hasOlderMessages == value) return;
            _hasOlderMessages = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private Contact(Peer? peer, ChatGroup? group = null)
    {
        Peer = peer;
        Group = group;
        if (group != null)
            group.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Subtitle));
            };
        if (peer != null)
            peer.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Subtitle));
                OnPropertyChanged(nameof(IsOnline));
                OnPropertyChanged(nameof(CanRemove));
                OnPropertyChanged(nameof(CanInviteToGame));
            };
    }

    public static Contact Everyone() => new(null);

    public static Contact For(Peer peer) => new(peer);

    public static Contact For(ChatGroup group) => new(null, group);

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
