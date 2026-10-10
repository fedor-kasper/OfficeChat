using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OfficeChat.Models;

/// <summary>Участник группы: постоянный Id компьютера и имя, под которым его видели последний раз.</summary>
public sealed record GroupMember(Guid Id, string Name);

/// <summary>
/// Групповой чат. Сервера нет, поэтому у каждого участника своя копия группы: название и состав
/// согласуются пакетами «создал», «добавил», «переименовал», «вышел», а сообщение уходит каждому участнику отдельно.
/// </summary>
public sealed class ChatGroup : INotifyPropertyChanged
{
    private string _name = "";
    private string _statusText = "";

    public required Guid Id { get; init; }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>Все участники, включая вас.</summary>
    public List<GroupMember> Members { get; set; } = new();

    /// <summary>«4 участника · в сети 2» — обновляет ChatService.</summary>
    public string StatusText
    {
        get => _statusText;
        set => Set(ref _statusText, value);
    }

    public bool HasMember(Guid id) => Members.Any(m => m.Id == id);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
