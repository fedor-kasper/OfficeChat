using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Views;

/// <summary>Касса: поставщик, новая запись (поставка, возврат, погашение) и история с балансом.</summary>
public partial class CashPanel : UserControl
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
    private static readonly Brush Dark = new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush Selected = new SolidColorBrush(Color.FromRgb(0xDB, 0xEA, 0xFE));

    private CashService? _cash;
    private Supplier? _supplier;
    private CashKind _kind = CashKind.Delivery;

    public CashPanel()
    {
        InitializeComponent();
        foreach (var box in new[] { DueBox, PaidBox, AmountBox, CommentBox })
        {
            if (box != CommentBox) box.TextChanged += (_, _) => UpdatePreview();
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) Record();
            };
        }
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

    /// <summary>Показать поставщика (и сразу — в поле суммы).</summary>
    public void Show(Supplier supplier)
    {
        if (_supplier != supplier) ClearForm();
        _supplier = supplier;
        Refresh();
        FocusAmount();
    }

    public Supplier? Current => _supplier;

    /// <summary>Поставщика убрали из списка — показать доску напоминаний.</summary>
    public event Action? Closed;

    private void Refresh()
    {
        if (_supplier is not { } s) return;
        NameText.Text = s.Name;
        BalanceText.Text = s.BalanceText;
        BalanceText.Foreground = s.Balance switch { > 0 => Green, < 0 => Red, _ => Dark };
        BalanceHint.Text = s.Balance switch
        {
            > 0 => $"Поставщик должен нам {s.BalanceText} — излишки и возвраты",
            < 0 => $"Мы должны поставщику {Money.Format(-s.Balance)}",
            _ => "Баланс нулевой",
        };
        RemoveButton.Visibility = s.CanRemove ? Visibility.Visible : Visibility.Collapsed;
        History.ItemsSource = s.Entries.AsEnumerable().Reverse().ToList();
        EmptyText.Visibility = s.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreview();
    }

    private void FocusAmount() => Dispatcher.BeginInvoke(() => (_kind == CashKind.Delivery ? DueBox : AmountBox).Focus(),
        System.Windows.Threading.DispatcherPriority.Input);

    // ---- Вид записи ----

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string tag && Enum.TryParse<CashKind>(tag, out var kind)) SelectTab(kind);
    }

    private void SelectTab(CashKind kind)
    {
        _kind = kind;
        foreach (var (tab, tabKind) in new[] { (DeliveryTab, CashKind.Delivery), (ReturnTab, CashKind.Return), (PayoutTab, CashKind.Payout) })
        {
            tab.Background = kind == tabKind ? Selected : Brushes.Transparent;
            tab.FontWeight = kind == tabKind ? FontWeights.SemiBold : FontWeights.Normal;
        }
        DeliveryFields.Visibility = kind == CashKind.Delivery ? Visibility.Visible : Visibility.Collapsed;
        AmountFields.Visibility = kind != CashKind.Delivery ? Visibility.Visible : Visibility.Collapsed;
        AmountLabel.Text = kind == CashKind.Return ? "Возврат на сумму, ₽" : "Сумма, ₽";
        TabHint.Text = kind switch
        {
            CashKind.Delivery => "Сколько по накладной и сколько дали водителю. Разница (округление) ляжет на баланс поставщика; " +
                                 "если дали меньше — спишется с баланса.",
            CashKind.Return => "Вернули товар — эти деньги поставщик должен нам: сумма прибавится к балансу.",
            _ => "Поставщик вернул деньги с баланса или учёл их в накладной — сумма спишется с баланса.",
        };
        UpdatePreview();
        FocusAmount();
    }

    private void RoundUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string tag || !long.TryParse(tag, out var step)) return;
        if (Money.Parse(DueBox.Text) is not { } due) return;
        PaidBox.Text = Money.FormatInput((due + step - 1) / step * step);
        PaidBox.Focus();
    }

    /// <summary>Что получится: «На баланс: +27,66 ₽ → будет 127,66 ₽».</summary>
    private void UpdatePreview()
    {
        if (RecordButton == null) return; // ещё строится разметка
        var delta = Delta(out var error);
        RecordButton.IsEnabled = _supplier != null && delta != null;
        if (delta is not { } d || _supplier == null)
        {
            PreviewText.Text = error;
            PreviewText.Foreground = error.Length > 0 ? Red : Muted;
            return;
        }
        var what = d switch { > 0 => "На баланс", < 0 => "С баланса", _ => "Баланс не меняется" };
        PreviewText.Text = d == 0 ? what : $"{what}: {Money.FormatSigned(d)} → будет {Money.Format(_supplier.Balance + d)}";
        PreviewText.Foreground = d >= 0 ? Green : Amber;
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

    private void Record_Click(object sender, RoutedEventArgs e) => Record();

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
        FocusAmount();
    }

    private void ClearForm() => DueBox.Text = PaidBox.Text = AmountBox.Text = CommentBox.Text = "";

    // ---- Действия ----

    private void CancelEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_cash == null || (sender as FrameworkElement)?.DataContext is not CashEntry entry) return;
        var reason = CashDialogs.Input(Window.GetWindow(this), "Отменить запись",
            $"{entry.Title}: {entry.Details} ({entry.DeltaText}).\nЗапись останется в истории зачёркнутой. Почему отменяете?",
            "", "Отменить запись", required: true);
        if (reason == null) return;
        _cash.Cancel(entry, reason);
        Refresh();
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_cash == null || _supplier == null) return;
        var name = CashDialogs.Input(Window.GetWindow(this), "Переименовать поставщика", "Название поставщика:", _supplier.Name,
            "Сохранить", required: true);
        if (name == null) return;
        _cash.Rename(_supplier, name);
        Refresh();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_cash == null || _supplier == null) return;
        if (MessageBox.Show(Window.GetWindow(this)!, $"Убрать «{_supplier.Name}» из списка поставщиков?", "Касса",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!_cash.Remove(_supplier)) return;
        _supplier = null;
        Closed?.Invoke();
    }
}
