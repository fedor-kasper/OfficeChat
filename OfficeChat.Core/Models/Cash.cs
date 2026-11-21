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
    /// <summary>Корректировка: баланс поставщика становится равным Balance (например, начать учёт с известной суммы).</summary>
    BalanceSet = 13,
    /// <summary>Заказ с сайта поставщика (External) не оплачиваем — убрать из списка ожидающих.</summary>
    OrderSkipped = 14,
    /// <summary>Отмена ошибочной записи TargetId (сама запись остаётся в журнале зачёркнутой).</summary>
    Cancel = 20,
    /// <summary>Поставщик подключён к сайту: Name — какой сайт, Login, Secret — пароль (зашифрован кодом кассы), Since — с какой даты учитывать.</summary>
    SupplierLinked = 30,
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
    /// <summary>Корректировка: каким стал баланс (может быть и минусом).</summary>
    [JsonPropertyName("balance")] public long Balance { get; init; }
    /// <summary>Заказ или возврат на сайте поставщика («forum:order:72211819») — чтобы не учесть его дважды.</summary>
    [JsonPropertyName("ext")] public string External { get; init; } = "";
    [JsonPropertyName("login")] public string Login { get; init; } = "";
    [JsonPropertyName("secret")] public string Secret { get; init; } = "";
    [JsonPropertyName("since")] public DateTimeOffset? Since { get; init; }
    /// <summary>Кто записал: имя в программе и компьютер.</summary>
    [JsonPropertyName("author")] public string Author { get; init; } = "";
    [JsonPropertyName("machine")] public string Machine { get; init; } = "";
    [JsonPropertyName("at")] public DateTimeOffset CreatedAt { get; init; }

    /// <summary>На сколько запись меняет баланс поставщика (без учёта отмены; у корректировки зависит от баланса до неё).</summary>
    [JsonIgnore]
    public long Delta => Kind switch
    {
        CashKind.Delivery => Paid - Due,
        CashKind.Return => Amount,
        CashKind.Payout => -Amount,
        _ => 0,
    };

    /// <summary>Запись в истории поставщика (её можно отменить).</summary>
    [JsonIgnore]
    public bool IsEntry => Kind is CashKind.Delivery or CashKind.Return or CashKind.Payout or CashKind.BalanceSet or CashKind.OrderSkipped;
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

    /// <summary>Как <see cref="Parse"/>, но можно и с минусом («-150», «−150»).</summary>
    public static long? ParseSigned(string? text)
    {
        var value = (text ?? "").Trim();
        var negative = value.StartsWith('-') || value.StartsWith('−');
        return Parse(negative ? value[1..] : value) is { } amount ? (negative ? -amount : amount) : null;
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

    /// <summary>Записи о деньгах (поставки, возвраты, погашения, корректировки), по времени.</summary>
    public List<CashEntry> Entries { get; } = new();

    /// <summary>По поставщику ещё нет ни одной действующей записи о деньгах — его можно убрать из списка.</summary>
    public bool CanRemove => Balance == 0 && Entries.All(e => !e.Counts);

    /// <summary>Подключение к сайту поставщика (последнее), null — не подключён.</summary>
    public CashOperation? Link { get; set; }

    /// <summary>Заказы и возвраты с сайта, уже учтённые в журнале (по номеру).</summary>
    public HashSet<string> Recorded { get; } = new();

    private List<ExternalOrder> _pendingOrders = new();

    /// <summary>Заказы с сайта, которые ещё ждут: сколько за них отдали.</summary>
    public List<ExternalOrder> PendingOrders
    {
        get => _pendingOrders;
        set
        {
            var had = _pendingOrders.Count;
            _pendingOrders = value;
            if (had == value.Count) return;
            OnPropertyChanged(nameof(HasPending));
            OnPropertyChanged(nameof(PendingText));
        }
    }

    public bool HasPending => _pendingOrders.Count > 0;

    /// <summary>Для списка поставщиков: «ждут оплаты: 2».</summary>
    public string PendingText => HasPending ? $"⚠ ждут оплаты: {_pendingOrders.Count}" : "";

    /// <summary>Заказы с сайта уже загружены (хотя бы раз с подключения).</summary>
    public bool SiteLoaded { get; set; }

    /// <summary>Что с сайтом: когда обновили, ошибка входа и т. п.</summary>
    public string SiteStatus { get; set; } = "";

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

    /// <summary>Насколько запись изменила баланс (у корректировки — до заданной суммы).</summary>
    public long Delta { get; set; }

    /// <summary>Запись отменили (кто, когда, почему).</summary>
    public CashOperation? Cancellation { get; set; }

    /// <summary>Тот же заказ или возврат с сайта уже учтён другой записью (например, с другого компьютера).</summary>
    public bool IsDuplicate { get; set; }

    public bool IsCancelled => Cancellation != null;

    /// <summary>Запись учитывается в балансе.</summary>
    public bool Counts => !IsCancelled && !IsDuplicate;

    /// <summary>Отменить можно действующую запись.</summary>
    public bool CanCancel => Counts;

    /// <summary>Бледная и зачёркнутая: отменена или повтор.</summary>
    public bool IsInactive => !Counts;

    public string Title => Operation.Kind switch
    {
        CashKind.Delivery => "Поставка",
        CashKind.Return => "Возврат товара",
        CashKind.Payout => "Погашение",
        CashKind.BalanceSet => "Корректировка баланса",
        CashKind.OrderSkipped => "Заказ не оплачиваем",
        _ => "",
    };

    /// <summary>«по накладной 672,34 ₽, выдано 700,00 ₽».</summary>
    public string Details => Operation.Kind switch
    {
        CashKind.Delivery => $"по накладной {Money.Format(Operation.Due)}, выдано {Money.Format(Operation.Paid)}",
        CashKind.Return => $"на сумму {Money.Format(Operation.Amount)}",
        CashKind.Payout => $"{Money.Format(Operation.Amount)} — вернули деньгами или учли в накладной",
        CashKind.BalanceSet => $"баланс стал {Money.Format(Operation.Balance)}",
        CashKind.OrderSkipped => "убран из ожидающих оплаты",
        _ => "",
    };

    public string DeltaText => Operation.Kind == CashKind.OrderSkipped ? "" : Money.FormatSigned(Delta);
    public bool IsPositive => Delta > 0;
    public bool IsNegative => Delta < 0;
    public string BalanceAfterText => IsCancelled ? "отменено" : IsDuplicate ? "повтор — уже учтено" : "баланс " + Money.Format(BalanceAfter);
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

/// <summary>Заказ на сайте поставщика: номер, когда оформлен, статус и сумма (копейки).</summary>
public sealed record ExternalOrder(string Number, DateTime Date, string Status, long Sum)
{
    public string External => "forum:order:" + Number;
    public string DateText => Date.ToString("dd.MM.yyyy HH:mm");
    public string SumText => Money.Format(Sum);
}

/// <summary>Возврат на сайте поставщика.</summary>
public sealed record ExternalReturn(string Number, DateTime Date, string Status, string Reason, string Penalty, long Sum)
{
    public string External => "forum:return:" + Number;
}
