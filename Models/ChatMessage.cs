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
    /// <summary>Изображение; <see cref="ChatMessage.Text"/> — подпись (может быть пустой).</summary>
    Image = 2,
    /// <summary>Файл любого типа; <see cref="ChatMessage.Text"/> — подпись (может быть пустой).</summary>
    File = 3,
    /// <summary>Служебная запись группы («создал группу», «вышел» и т. п.) — по сети не передаётся.</summary>
    Service = 4,
}

/// <summary>Одно сообщение в личной переписке или группе (входящее или исходящее).</summary>
public sealed class ChatMessage : INotifyPropertyChanged
{
    private MessageStatus _status;
    private bool _peerOffline;
    private bool _isRead;

    public required Guid Id { get; init; }
    public required bool IsOutgoing { get; init; }
    /// <summary>Текст (у изображения и файла — подпись). Меняется, если сообщение отредактировали.</summary>
    public required string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            _hasLinks = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasText));
            OnPropertyChanged(nameof(HasLinks));
            OnPropertyChanged(nameof(HasPlainText));
            OnPropertyChanged(nameof(PreviewText));
        }
    }
    private string _text = "";
    public required DateTime Timestamp { get; init; }

    public MessageKind Kind { get; init; }

    public bool IsGame => Kind == MessageKind.Game;

    public bool IsService => Kind == MessageKind.Service;

    /// <summary>Запись, которую делает сама программа (итог игры, события группы), а не человек.</summary>
    public bool IsSystem => IsGame || IsService;

    // ---- Группы ----

    /// <summary>Кто прислал сообщение в группу (для личной переписки — null).</summary>
    public Guid? SenderId { get; init; }

    /// <summary>Имя автора сообщения в группе.</summary>
    public string SenderName { get; init; } = "";

    /// <summary>Подписывать ли пузырёк именем автора (входящие в группе).</summary>
    public bool ShowSender => !IsOutgoing && SenderName.Length > 0 && !IsSystem;

    /// <summary>Своё сообщение в группе: скольким участникам оно адресовано (0 — личное сообщение).</summary>
    public int RecipientCount { get; set; }

    public bool IsGroupOutgoing => IsOutgoing && RecipientCount > 0;

    /// <summary>Скольким участникам группы сообщение уже доставлено.</summary>
    public int DeliveredCount
    {
        get => _deliveredCount;
        set
        {
            if (Set(ref _deliveredCount, value)) OnStatusChanged();
        }
    }
    private int _deliveredCount;

    /// <summary>Сколько участников группы его прочитали.</summary>
    public int ReadCount
    {
        get => _readCount;
        set
        {
            if (Set(ref _readCount, value)) OnStatusChanged();
        }
    }
    private int _readCount;

    public bool IsImage => Kind == MessageKind.Image;

    public bool IsFile => Kind == MessageKind.File;

    /// <summary>Где на диске лежит файл (для сообщений-файлов).</summary>
    public string FilePath { get; init; } = "";

    public long FileSize { get; init; }

    public string FileSizeText => FileSize switch
    {
        >= 1024L * 1024 * 1024 => $"{FileSize / 1024.0 / 1024 / 1024:0.##} ГБ",
        >= 1024 * 1024 => $"{FileSize / 1024.0 / 1024:0.#} МБ",
        _ => $"{Math.Max(1, FileSize / 1024)} КБ",
    };

    /// <summary>Значок файла по расширению.</summary>
    public string FileIcon => System.IO.Path.GetExtension(FileName).ToLowerInvariant() switch
    {
        ".pdf" => "📕",
        ".doc" or ".docx" or ".odt" or ".rtf" or ".txt" => "📝",
        ".xls" or ".xlsx" or ".ods" or ".csv" => "📊",
        ".ppt" or ".pptx" or ".odp" => "📽",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "🗜",
        ".mp3" or ".wav" or ".ogg" or ".flac" or ".m4a" => "🎵",
        ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" => "🎬",
        ".exe" or ".msi" or ".appimage" or ".deb" => "⚙",
        _ => "📄",
    };

    /// <summary>Сколько отправлено (0…1), пока идёт передача вложения.</summary>
    public double TransferProgress
    {
        get => _transferProgress;
        set
        {
            if (Set(ref _transferProgress, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }
    private double _transferProgress;
    /// <summary>Исходное имя файла изображения (для «Сохранить как…»).</summary>
    public string FileName { get; init; } = "";

    /// <summary>Где на диске лежит изображение этого сообщения.</summary>
    public string ImagePath { get; init; } = "";

    public bool HasText => !string.IsNullOrEmpty(Text);

    /// <summary>В тексте есть ссылки — показываем его с кликабельными ссылками.</summary>
    public bool HasLinks => _hasLinks ??= !IsSystem && Services.LinkParser.ContainsLink(Text);
    private bool? _hasLinks;

    /// <summary>Текст без ссылок — показываем как обычно (с выделением).</summary>
    public bool HasPlainText => HasText && !HasLinks;

    /// <summary>Короткий текст для уведомлений: у изображения — значок и подпись.</summary>
    public string PreviewText => Kind switch
    {
        MessageKind.Image => HasText ? $"🖼 {Text}" : "🖼 Изображение",
        MessageKind.File => HasText ? $"📎 {FileName}: {Text}" : $"📎 {FileName}",
        _ => Text,
    };

    /// <summary>Сообщение было отправлено «Всем», а не лично.</summary>
    public bool IsBroadcast { get; init; }

    // ---- Ответ, правка ----

    /// <summary>На какое сообщение это ответ (null — не ответ).</summary>
    public Guid? ReplyToId { get; init; }

    /// <summary>Автор сообщения, на которое ответили («Вы» — если на ваше).</summary>
    public string ReplyAuthor { get; init; } = "";

    /// <summary>Начало текста сообщения, на которое ответили (сохраняется, даже если оригинал удалят).</summary>
    public string ReplyText { get; init; } = "";

    public bool HasReply => ReplyToId != null;

    /// <summary>Сообщение отредактировано после отправки.</summary>
    public bool IsEdited
    {
        get => _isEdited;
        set
        {
            if (Set(ref _isEdited, value))
                OnPropertyChanged(nameof(TimeLabel));
        }
    }
    private bool _isEdited;

    /// <summary>Ответить можно на любое сообщение, кроме служебных записей.</summary>
    public bool CanReply => !IsSystem;

    /// <summary>Изменить можно свой текст или подпись к вложению.</summary>
    public bool CanEdit => IsOutgoing && !IsSystem;

    /// <summary>Удалить у собеседника можно только своё сообщение.</summary>
    public bool CanDeleteForEveryone => IsOutgoing && !IsSystem;

    /// <summary>Короткая цитата для ответа: начало текста или описание вложения.</summary>
    public string QuoteText
    {
        get
        {
            var text = PreviewText.ReplaceLineEndings(" ");
            return text.Length > MaxQuoteLength ? text[..MaxQuoteLength] + "…" : text;
        }
    }

    public const int MaxQuoteLength = 150;

    // ---- Исходящие ----

    public MessageStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value)) OnStatusChanged();
        }
    }

    private void OnStatusChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanCancel));
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

    /// <summary>Почему не уходит, если адресат в сети, но доставка не удалась (пусто — причина неизвестна).</summary>
    public string FailureHint
    {
        get => _failureHint;
        set
        {
            if (Set(ref _failureHint, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }
    private string _failureHint = "";

    /// <summary>Отменить можно только то, что ещё не ушло адресату (в группе — ни одному участнику).</summary>
    public bool CanCancel => IsOutgoing && Status == MessageStatus.Queued && DeliveredCount == 0;

    public string StatusText => !IsOutgoing ? "" : IsGroupOutgoing ? GroupStatusText : Status switch
    {
        MessageStatus.Queued => PeerOffline
            ? "⏳ Адресат не в сети — отправится, когда он появится"
            : FailureHint.Length > 0
                ? $"⏳ {FailureHint} — повторяем…"
                : "⏳ Не удалось отправить — повторяем…",
        MessageStatus.Sending => TransferProgress > 0 && TransferProgress < 1
            ? $"Отправляется… {TransferProgress:0%}"
            : "Отправляется…",
        MessageStatus.Delivered => "✓ Доставлено",
        MessageStatus.Read => "✓✓ Прочитано",
        _ => "",
    };

    private string GroupStatusText => Status switch
    {
        MessageStatus.Sending => TransferProgress > 0 && TransferProgress < 1
            ? $"Отправляется… {TransferProgress:0%}"
            : "Отправляется…",
        _ when ReadCount >= RecipientCount => "✓✓ Прочитано всеми",
        _ when ReadCount > 0 => $"✓✓ Прочитали {ReadCount} из {RecipientCount}",
        _ when DeliveredCount >= RecipientCount => "✓ Доставлено",
        _ when DeliveredCount > 0 => $"✓ Доставлено {DeliveredCount} из {RecipientCount}",
        _ => "⏳ Участники не в сети — отправится, когда появятся",
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

    /// <summary>Время под сообщением, с пометкой «изменено» для отредактированных.</summary>
    public string TimeLabel => IsEdited ? $"изменено {TimeText}" : TimeText;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name!);
        return true;
    }
}
