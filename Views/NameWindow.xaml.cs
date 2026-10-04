using System.Windows;
using System.Windows.Controls;

namespace OfficeChat.Views;

public partial class NameWindow : Window
{
    public string EnteredName => NameBox.Text.Trim();

    public NameWindow(string initialName)
    {
        InitializeComponent();
        NameBox.Text = initialName;
        NameBox.SelectAll();
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e) =>
        OkButton.IsEnabled = EnteredName.Length > 0;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
