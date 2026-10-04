using System.ComponentModel;
using System.Net;
using System.Runtime.CompilerServices;

namespace OfficeChat.Models;

/// <summary>Компьютер в сети, на котором запущен OfficeChat.</summary>
public sealed class Peer : INotifyPropertyChanged
{
    private string _name = "";
    private string _machine = "";
    private IPAddress _address = IPAddress.None;
    private bool _isOnline;

    public required Guid Id { get; init; }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string Machine
    {
        get => _machine;
        set => Set(ref _machine, value);
    }

    public IPAddress Address
    {
        get => _address;
        set => Set(ref _address, value);
    }

    public bool IsOnline
    {
        get => _isOnline;
        set => Set(ref _isOnline, value);
    }

    public DateTime LastSeen { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
