using System.ComponentModel;

namespace OfficeChat.Models;

/// <summary>Строка в списке слева: либо конкретный компьютер, либо «Все».</summary>
public sealed class Contact : INotifyPropertyChanged
{
    /// <summary>Компьютер собеседника; null — общий чат со всеми.</summary>
    public Peer? Peer { get; }

    public bool IsEveryone => Peer == null;

    public string Title => Peer?.Name ?? "Все";

    public string Subtitle => Peer == null
        ? "Сообщение получат все в сети"
        : $"{Peer.Machine} · {Peer.Address}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private Contact(Peer? peer)
    {
        Peer = peer;
        if (peer != null)
            peer.PropertyChanged += (_, _) =>
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Subtitle)));
            };
    }

    public static Contact Everyone() => new(null);

    public static Contact For(Peer peer) => new(peer);
}
