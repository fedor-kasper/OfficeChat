using System.Collections.ObjectModel;
using System.Collections.Specialized;
using OfficeChat.Services;

namespace OfficeChat.Models;

/// <summary>
/// Сколько сообщений переписки показывать на экране: при открытии — только последние, остальные — по кнопке
/// «Показать раньше» (сначала из уже загруженных, потом из истории на диске). Без этого переписка с большой
/// историей открывалась долго: на экране строились сразу все сотни сообщений с картинками.
/// <see cref="Items"/> следит за <see cref="Contact.Messages"/>: новые сообщения появляются, удалённые пропадают.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    /// <summary>Сколько последних сообщений показывать при открытии.</summary>
    public const int InitialCount = 40;

    /// <summary>Сколько добавлять по «Показать раньше».</summary>
    public const int Step = 60;

    private readonly Contact _contact;
    // Индекс в Contact.Messages первого показанного сообщения (всё, что раньше, — скрыто).
    private int _start;

    /// <summary>Сообщения на экране — по порядку, как в переписке.</summary>
    public ObservableCollection<ChatMessage> Items { get; } = new();

    /// <summary>Есть что показать раньше — в памяти или в истории на диске.</summary>
    public bool HasMore => _start > 0 || _contact.HasOlderMessages;

    /// <summary>Поменялось <see cref="HasMore"/>.</summary>
    public event Action? HasMoreChanged;

    public MessageWindow(Contact contact)
    {
        _contact = contact;
        _start = Math.Max(0, contact.Messages.Count - InitialCount);
        for (var i = _start; i < contact.Messages.Count; i++) Items.Add(contact.Messages[i]);
        contact.Messages.CollectionChanged += OnSourceChanged;
        contact.PropertyChanged += OnContactChanged;
    }

    /// <summary>Показать ещё порцию более ранних (при нехватке — подгрузив их из истории).</summary>
    public void ShowMore(ChatService chat)
    {
        if (_start == 0 && _contact.HasOlderMessages) chat.LoadOlder(_contact);
        RevealTo(Math.Max(0, _start - Step));
    }

    /// <summary>Показать сообщение (например, на которое ответили), если оно уже загружено. false — его нет.</summary>
    public bool Reveal(ChatMessage message)
    {
        var index = _contact.Messages.IndexOf(message);
        if (index < 0) return false;
        RevealTo(index);
        return true;
    }

    private void RevealTo(int start)
    {
        if (start >= _start) return;
        for (var i = _start - 1; i >= start; i--) Items.Insert(0, _contact.Messages[i]);
        _start = start;
        HasMoreChanged?.Invoke();
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var before = HasMore;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems != null:
                // Раньше первого показанного (подгрузили более ранние или пришло старое сообщение) — в скрытую часть.
                if (e.NewStartingIndex < _start || (e.NewStartingIndex == _start && Items.Count > 0))
                {
                    _start += e.NewItems.Count;
                    break;
                }
                var at = e.NewStartingIndex - _start;
                foreach (ChatMessage message in e.NewItems) Items.Insert(Math.Min(at++, Items.Count), message);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems != null:
                if (e.OldStartingIndex < _start)
                    _start = Math.Max(0, _start - e.OldItems.Count);
                else
                    foreach (ChatMessage message in e.OldItems) Items.Remove(message);
                break;
            case NotifyCollectionChangedAction.Replace when e.OldItems != null && e.NewItems != null:
                for (var i = 0; i < e.OldItems.Count; i++)
                    if (Items.IndexOf((ChatMessage)e.OldItems[i]!) is var index and >= 0)
                        Items[index] = (ChatMessage)e.NewItems[i]!;
                break;
            default:
                // Очистили или переставили — показываем заново последние.
                Items.Clear();
                _start = Math.Max(0, _contact.Messages.Count - InitialCount);
                for (var i = _start; i < _contact.Messages.Count; i++) Items.Add(_contact.Messages[i]);
                break;
        }
        if (HasMore != before) HasMoreChanged?.Invoke();
    }

    private void OnContactChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Contact.HasOlderMessages)) HasMoreChanged?.Invoke();
    }

    public void Dispose()
    {
        _contact.Messages.CollectionChanged -= OnSourceChanged;
        _contact.PropertyChanged -= OnContactChanged;
    }
}
