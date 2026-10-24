using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace OfficeChat.Models;

/// <summary>Вид записи в журнале кассы.</summary>
public enum CashKind
{
    /// <summary>Новый поставщик.</summary>
    SupplierAdded = 1,
    /// <summary>Поставщика переименовали.</summary>
    SupplierRenamed = 2,
    /// <summary>Поставщика убрали из списка (только без движения денег).</summary>
    SupplierRemoved = 3,
    /// <summary>Поставка: по накладной Due, выдали Paid; разница — на баланс (или с баланса).</summary>
    Delivery = 10,
    /// <summary>Возврат товара на сумму Amount: поставщик должен нам эти деньги — плюс к балансу.</summary>
    Return = 11,
    /// <summary>Погашение: поставщик вернул деньги или учёл их в накладной — минус Amount с баланса.</summary>
    Payout = 12,
    /// <summary>Отмена ошибочной записи TargetId (сама запись остаётся в журнале зачёркнутой).</summary>
    Cancel = 20,
}

/// <summary>
/// Запись журнала кассы. Журнал только дополняется: записи не меняются и не удаляются — ошибку исправляют
/// отменой. Поэтому компьютеры кассы синхронизируются простым обменом недостающими записями, без сервера.
/// Суммы — в копейках.
/// </summary>
public sealed record CashOperation
{
    [JsonPropertyName("id")] public Guid Id { get; init; }
    /// <summary>Компьютер (профиль), где сделана запись, и её номер по порядку на нём (1, 2, 3…).</summary>
    [JsonPropertyName("dev")] public Guid DeviceId { get; init; }
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("kind")] public CashKind Kind { get; init; }
    [JsonPropertyName("sup")] public Guid SupplierId { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("due")] public long Due { get; init; }
    [JsonPropertyName("paid")] public long Paid { get; init; }
    [JsonPropertyName("amount")] public long Amount { get; init; }
    [JsonPropertyName("comment")] public string Comment { get; init; } = "";
    [JsonPropertyName("target")] public Guid TargetId { get; init; }
    /// <summary>Кто записал: имя в программе и компьютер.</summary>
    [JsonPropertyName("author")] public string Author { get; init; } = "";
    [JsonPropertyName("machine")] public string Machine { get; init; } = "";
    [JsonPropertyName("at")] public DateTimeOffset CreatedAt { get; init; }

    /// <summary>На сколько запись меняет баланс поставщика (без учёта отмены).</summary>
    [JsonIgnore]
    public long Delta => Kind switch
    {
        CashKind.Delivery => Paid - Due,
        CashKind.Return => Amount,
        CashKind.Payout => -Amount,
        _ => 0,
    };

    /// <summary>Запись про деньги (её можно отменить).</summary>
    [JsonIgnore]
    public bool IsMoney => Kind is CashKind.Delivery or CashKind.Return or CashKind.Payout;
}

/// <summary>Деньги в копейках: разбор того, что ввели, и вывод «1 234,56 ₽».</summary>
public static class Money
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Не больше 100 миллионов рублей за одну запись — защита от лишних нулей.</summary>
    public const long MaxAmount = 100_000_000_00;

    /// <summary>«672.34», «672,34», «1 234», «700 р» → копейки. null — не число или больше двух знаков после запятой.</summary>
    public static long? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cleaned = new string(text.Where(c => !char.IsWhiteSpace(c) && c != ' ').ToArray())
            .TrimEnd('₽', 'р', 'Р', '.').Replace(',', '.');
        if (cleaned.EndsWith("руб", StringComparison.OrdinalIgnoreCase)) cleaned = cleaned[..^3];
        if (!decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        if (decimal.Round(value, 2) != value) return null;
        var kopecks = (long)(value * 100);
        return kopecks > MaxAmount ? null : kopecks;
    }

    /// <summary>«1 234,56 ₽».</summary>
    public static string Format(long kopecks) => (kopecks / 100m).ToString("#,0.00", Russian) + " ₽";

    /// <summary>Со знаком: «+27,66 ₽», «−20,00 ₽», «0,00 ₽».</summary>
    public static string FormatSigned(long kopecks) => kopecks switch
    {
        > 0 => "+" + Format(kopecks),
        < 0 => "−" + Format(-kopecks),
        _ => Format(0),
    };

    /// <summary>Без «₽» — для поля ввода.</summary>
    public static string FormatInput(long kopecks) => (kopecks / 100m).ToString("0.00", Russian);
}

/// <summary>Поставщик в списке кассы: имя и баланс (сколько он нам должен — излишки и возвраты).</summary>
public sealed class Supplier : INotifyPropertyChanged
{
    private string _name = "";
    private long _balance;
    private DateTimeOffset _lastActivity;

    public required Guid Id { get; init; }

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    /// <summary>Баланс в копейках: плюс — поставщик должен нам, минус — мы ему.</summary>
    public long Balance
    {
        get => _balance;
        set
        {
            if (!Set(ref _balance, value)) return;
            OnPropertyChanged(nameof(BalanceText));
            OnPropertyChanged(nameof(IsPositive));
            OnPropertyChanged(nameof(IsNegative));
        }
    }

    public string BalanceText => Money.Format(Balance);
    public bool IsPositive => Balance > 0;
    public bool IsNegative => Balance < 0;

    /// <summary>Последняя запись по поставщику — список упорядочен по ней.</summary>
    public DateTimeOffset LastActivity
    {
        get => _lastActivity;
        set => Set(ref _lastActivity, value);
    }

    /// <summary>Записи о деньгах (поставки, возвраты, погашения), по времени.</summary>
    public List<CashEntry> Entries { get; } = new();

    /// <summary>По поставщику ещё нет ни одной действующей записи о деньгах — его можно убрать из списка.</summary>
    public bool CanRemove => Balance == 0 && Entries.All(e => e.IsCancelled);

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Строка истории поставщика: запись, баланс после неё и отмена, если была.</summary>
public sealed class CashEntry
{
    public required CashOperation Operation { get; init; }

    /// <summary>Баланс поставщика после этой записи.</summary>
    public long BalanceAfter { get; set; }

    /// <summary>Запись отменили (кто, когда, почему).</summary>
    public CashOperation? Cancellation { get; set; }

    public bool IsCancelled => Cancellation != null;

    public string Title => Operation.Kind switch
    {
        CashKind.Delivery => "Поставка",
        CashKind.Return => "Возврат товара",
        CashKind.Payout => "Погашение",
        _ => "",
    };

    /// <summary>«по накладной 672,34 ₽, выдано 700,00 ₽».</summary>
    public string Details => Operation.Kind switch
    {
        CashKind.Delivery => $"по накладной {Money.Format(Operation.Due)}, выдано {Money.Format(Operation.Paid)}",
        CashKind.Return => $"на сумму {Money.Format(Operation.Amount)}",
        CashKind.Payout => $"{Money.Format(Operation.Amount)} — вернули деньгами или учли в накладной",
        _ => "",
    };

    public string DeltaText => Money.FormatSigned(Operation.Delta);
    public bool IsPositive => Operation.Delta > 0;
    public bool IsNegative => Operation.Delta < 0;
    public string BalanceAfterText => IsCancelled ? "отменено" : "баланс " + Money.Format(BalanceAfter);
    public bool HasComment => Operation.Comment.Length > 0;
    public string Comment => Operation.Comment;

    /// <summary>«24.10.2026 14:05 · Иван (KASSA-1)».</summary>
    public string AuthorLine =>
        $"{Operation.CreatedAt.LocalDateTime:dd.MM.yyyy HH:mm} · {Who(Operation)}";

    public string CancelLine => Cancellation is { } c
        ? $"Отменено {c.CreatedAt.LocalDateTime:dd.MM.yyyy HH:mm} · {Who(c)}" + (c.Comment.Length > 0 ? $": {c.Comment}" : "")
        : "";

    private static string Who(CashOperation op) =>
        op.Machine.Length > 0 && op.Machine != op.Author ? $"{op.Author} ({op.Machine})" : op.Author;
}
