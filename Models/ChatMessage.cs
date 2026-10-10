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
    public bool HasLinks => _hasLinks ??= !IsGame && Services.LinkParser.ContainsLink(Text);
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

    /// <summary>Отменить можно только то, что ещё не ушло адресату.</summary>
    public bool CanCancel => IsOutgoing && Status == MessageStatus.Queued;

    public string StatusText => !IsOutgoing ? "" : Status switch
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
