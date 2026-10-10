using Avalonia.Controls;
using Avalonia.Interactivity;

namespace OfficeChat.Views;

public partial class SettingsWindow : Window
{
    public string EnteredName => NameBox.Text?.Trim() ?? "";

    public bool AutoStart => AutoStartBox.IsChecked == true;

    /// <summary>Нажали «Сохранить».</summary>
    public bool Saved { get; private set; }

    public SettingsWindow() : this("", true) { }

    public SettingsWindow(string initialName, bool autoStart)
    {
        InitializeComponent();
        Icon = Platform.AppIcon.Window;
        NameBox.Text = initialName;
        AutoStartBox.IsChecked = autoStart;
        NameBox.TextChanged += (_, _) => OkButton.IsEnabled = EnteredName.Length > 0;
        OkButton.IsEnabled = EnteredName.Length > 0;
        Opened += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>Номер версии внизу окна (и обновляется ли программа по сети).</summary>
    public void ShowVersion(Version version, bool signed)
    {
        VersionText.Text = $"OfficeChat {version}" + (signed
            ? " · обновляется сам, когда у коллег появляется новая версия"
            : " · сборка без подписи, обновление по сети выключено");
        VersionText.IsVisible = true;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (EnteredName.Length == 0) return;
        Saved = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();
}
