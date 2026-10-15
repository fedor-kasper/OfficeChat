using System.Windows;
using System.Windows.Controls;

namespace OfficeChat.Views;

public partial class SettingsWindow : Window
{
    public string EnteredName => NameBox.Text.Trim();

    public bool AutoStart => AutoStartBox.IsChecked == true;

    public SettingsWindow(string initialName, bool autoStart)
    {
        InitializeComponent();
        NameBox.Text = initialName;
        NameBox.SelectAll();
        AutoStartBox.IsChecked = autoStart;
    }

    /// <summary>Номер версии внизу окна (и обновляется ли программа по сети).</summary>
    public void ShowVersion(Version version, bool signed)
    {
        VersionText.Text = $"OfficeChat {version}" + (signed
            ? " · обновляется сам, когда у коллег появляется новая версия"
            : " · сборка без подписи, обновление по сети выключено");
        VersionText.Visibility = Visibility.Visible;
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) =>
        OkButton.IsEnabled = EnteredName.Length > 0;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
