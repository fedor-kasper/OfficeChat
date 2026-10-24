using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>Касса: поставщик, новая запись (поставка, возврат, погашение) и история с балансом.</summary>
public partial class CashPanel : UserControl
{
    private CashService? _cash;
    private Supplier? _supplier;
    private CashKind _kind = CashKind.Delivery;

    public CashPanel()
    {
        InitializeComponent();
        foreach (var box in new[] { DueBox, PaidBox, AmountBox })
        {
            box.TextChanged += (_, _) => UpdatePreview();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) Record();
            };
        }
        CommentBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Record();
        };
        SelectTab(CashKind.Delivery);
    }

    public void Attach(CashService cash)
    {
        _cash = cash;
        cash.Changed += () =>
        {
            if (_supplier != null && IsVisible) Refresh();
        };
    }

    /// <summary>Показать поставщика (и сразу — в поле суммы по накладной).</summary>
    public void Show(Supplier supplier)
    {
        var other = _supplier != supplier;
        _supplier = supplier;
        if (other) ClearForm();
        Refresh();
        (_kind == CashKind.Delivery ? DueBox : AmountBox).Focus();
    }

    public Supplier? Current => _supplier;

    private void Refresh()
    {
        if (_supplier is not { } s) return;
        NameText.Text = s.Name;
        BalanceText.Text = s.BalanceText;
        BalanceText.Foreground = s.Balance switch
        {
            > 0 => new SolidColorBrush(Color.Parse("#15803D")),
            < 0 => new SolidColorBrush(Color.Parse("#B91C1C")),
            _ => new SolidColorBrush(Color.Parse("#111827")),
        };
        BalanceHint.Text = s.Balance switch
        {
            > 0 => $"Поставщик должен нам {s.BalanceText} — излишки и возвраты",
            < 0 => $"Мы должны поставщику {Money.Format(-s.Balance)}",
            _ => "Баланс нулевой",
        };
        RemoveButton.IsVisible = s.CanRemove;
        History.ItemsSource = s.Entries.AsEnumerable().Reverse().ToList();
        EmptyText.IsVisible = s.Entries.Count == 0;
        UpdatePreview();
    }

    // ---- Вид записи ----

    private void Tab_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string tag && Enum.TryParse<CashKind>(tag, out var kind)) SelectTab(kind);
    }

    private void SelectTab(CashKind kind)
    {
        _kind = kind;
        DeliveryTab.Classes.Set("selected", kind == CashKind.Delivery);
        ReturnTab.Classes.Set("selected", kind == CashKind.Return);
        PayoutTab.Classes.Set("selected", kind == CashKind.Payout);
        DeliveryFields.IsVisible = kind == CashKind.Delivery;
        AmountFields.IsVisible = kind != CashKind.Delivery;
        AmountLabel.Text = kind == CashKind.Return ? "Возврат на сумму, ₽" : "Сумма, ₽";
        TabHint.Text = kind switch
        {
            CashKind.Delivery => "Сколько по накладной и сколько дали водителю. Разница (округление) ляжет на баланс поставщика; " +
                                 "если дали меньше — спишется с баланса.",
            CashKind.Return => "Вернули товар — эти деньги поставщик должен нам: сумма прибавится к балансу.",
            _ => "Поставщик вернул деньги с баланса или учёл их в накладной — сумма спишется с баланса.",
        };
        UpdatePreview();
        (kind == CashKind.Delivery ? DueBox : AmountBox).Focus();
    }

    private void RoundUp_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string tag || !long.TryParse(tag, out var step)) return;
        if (Money.Parse(DueBox.Text) is not { } due) return;
        PaidBox.Text = Money.FormatInput((due + step - 1) / step * step);
        PaidBox.Focus();
    }

    /// <summary>Что получится: «На баланс: +27,66 ₽ → будет 127,66 ₽».</summary>
    private void UpdatePreview()
    {
        var delta = Delta(out var error);
        RecordButton.IsEnabled = _supplier != null && delta != null;
        if (delta is not { } d || _supplier == null)
        {
            PreviewText.Text = error;
            PreviewText.Foreground = new SolidColorBrush(Color.Parse(error.Length > 0 ? "#B91C1C" : "#6B7280"));
            return;
        }
        var what = d switch { > 0 => "На баланс", < 0 => "С баланса", _ => "Баланс не меняется" };
        PreviewText.Text = d == 0 ? what : $"{what}: {Money.FormatSigned(d)} → будет {Money.Format(_supplier.Balance + d)}";
        PreviewText.Foreground = new SolidColorBrush(Color.Parse(d >= 0 ? "#15803D" : "#B45309"));
    }

    /// <summary>Изменение баланса по введённому (null — ввели не всё или с ошибкой, error — что не так).</summary>
    private long? Delta(out string error)
    {
        error = "";
        if (_kind == CashKind.Delivery)
        {
            var due = Money.Parse(DueBox.Text);
            var paid = Money.Parse(PaidBox.Text);
            if (!string.IsNullOrWhiteSpace(DueBox.Text) && due == null) error = "Сумма по накладной — число, например 672,34";
            else if (!string.IsNullOrWhiteSpace(PaidBox.Text) && paid == null) error = "Выдано — число, например 700";
            return due is { } dueValue && paid is { } paidValue ? paidValue - dueValue : null;
        }
        var amount = Money.Parse(AmountBox.Text);
        if (!string.IsNullOrWhiteSpace(AmountBox.Text) && (amount == null || amount == 0)) error = "Сумма — число больше нуля";
        return amount is > 0 ? (_kind == CashKind.Return ? amount : -amount) : null;
    }

    private void Record_Click(object? sender, RoutedEventArgs e) => Record();

    private void Record()
    {
        if (_cash == null || _supplier == null || Delta(out _) == null) return;
        var comment = CommentBox.Text ?? "";
        switch (_kind)
        {
            case CashKind.Delivery:
                _cash.RecordDelivery(_supplier, Money.Parse(DueBox.Text)!.Value, Money.Parse(PaidBox.Text)!.Value, comment);
                break;
            case CashKind.Return:
                _cash.RecordReturn(_supplier, Money.Parse(AmountBox.Text)!.Value, comment);
                break;
            default:
                _cash.RecordPayout(_supplier, Money.Parse(AmountBox.Text)!.Value, comment);
                break;
        }
        ClearForm();
        Refresh();
        (_kind == CashKind.Delivery ? DueBox : AmountBox).Focus();
    }

    private void ClearForm()
    {
        DueBox.Text = PaidBox.Text = AmountBox.Text = CommentBox.Text = "";
    }

    // ---- Действия ----

    private async void CancelEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (_cash == null || (sender as Control)?.DataContext is not CashEntry entry || TopLevel.GetTopLevel(this) is not Window owner)
            return;
        var reason = await Dialogs.Input(owner, "Отменить запись",
            $"{entry.Title}: {entry.Details} ({entry.DeltaText}).\nЗапись останется в истории зачёркнутой. Почему отменяете?",
            "", "Отменить запись", required: true);
        if (reason == null) return;
        _cash.Cancel(entry, reason);
        Refresh();
    }

    private async void Rename_Click(object? sender, RoutedEventArgs e)
    {
        if (_cash == null || _supplier == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        var name = await Dialogs.Input(owner, "Переименовать поставщика", "Название поставщика:", _supplier.Name, "Сохранить",
            required: true);
        if (name == null) return;
        _cash.Rename(_supplier, name);
        Refresh();
    }

    /// <summary>Поставщика убрали из списка — показать доску напоминаний.</summary>
    public event Action? Closed;

    private async void Remove_Click(object? sender, RoutedEventArgs e)
    {
        if (_cash == null || _supplier == null || TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!await Dialogs.Confirm(owner, $"Убрать «{_supplier.Name}» из списка поставщиков?")) return;
        if (_cash.Remove(_supplier))
        {
            _supplier = null;
            Closed?.Invoke();
        }
    }
}
