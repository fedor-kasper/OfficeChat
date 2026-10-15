using System.Windows;
using OfficeChat.Protocol;

namespace OfficeChat.Views;

/// <summary>Смена пароля аккаунта: текущий и новый (дважды).</summary>
public partial class ChangePasswordWindow : Window
{
    public string OldPassword => OldBox.Password;
    public string NewPassword => NewBox.Password;

    public ChangePasswordWindow()
    {
        InitializeComponent();
        Icon = AppIcon.CreateImageSource();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var error = OldBox.Password.Length == 0 ? "Введите текущий пароль"
            : NewBox.Password.Length < ServerProtocol.MinPasswordLength
                ? $"Новый пароль — не короче {ServerProtocol.MinPasswordLength} символов"
            : NewBox.Password != RepeatBox.Password ? "Новые пароли не совпадают"
            : null;
        if (error != null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }
}
