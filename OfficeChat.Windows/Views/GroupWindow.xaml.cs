using System.Windows;
using System.Windows.Controls;
using OfficeChat.Models;

namespace OfficeChat.Views;

/// <summary>
/// Создание группы (название и кого позвать) или её настройка: переименовать и добавить участников.
/// Убрать участника нельзя — выйти из группы каждый может сам.
/// </summary>
public partial class GroupWindow : Window
{
    private readonly List<(CheckBox Box, Contact Person)> _choices = new();
    private readonly bool _creating;

    public string EnteredName => NameBox.Text.Trim();

    /// <summary>Кого отметили (новых участников).</summary>
    public List<Contact> SelectedPeople =>
        _choices.Where(c => c.Box.IsChecked == true).Select(c => c.Person).ToList();

    /// <param name="group">null — создаётся новая группа.</param>
    public GroupWindow(ChatGroup? group, IEnumerable<Contact> people, Func<Guid, bool> isOnline, Guid myId)
    {
        InitializeComponent();
        _creating = group == null;
        HeaderText.Text = _creating ? "Новая группа" : "Группа";
        OkButton.Content = _creating ? "Создать" : "Сохранить";
        MembersLabel.Text = _creating ? "Кого позвать" : "Участники (отметьте, кого добавить)";
        NameBox.Text = group?.Name ?? "";
        NameBox.SelectAll();

        if (group != null)
        {
            // Нынешние участники: отмечены, убрать нельзя.
            foreach (var member in group.Members.OrderBy(m => m.Id != myId).ThenBy(m => m.Name))
            {
                var label = member.Id == myId ? $"{member.Name} (вы)" : $"{member.Name} · {(isOnline(member.Id) ? "в сети" : "не в сети")}";
                PeopleList.Children.Add(new CheckBox { Content = label, IsChecked = true, IsEnabled = false, Margin = new Thickness(0, 3, 0, 3) });
            }
        }
        foreach (var person in people.Where(p => group == null || !group.HasMember(p.Key)))
        {
            var box = new CheckBox
            {
                Content = $"{person.Title} · {(person.IsOnline ? "в сети" : "не в сети")}",
                Margin = new Thickness(0, 3, 0, 3),
            };
            box.Checked += (_, _) => Validate();
            box.Unchecked += (_, _) => Validate();
            _choices.Add((box, person));
            PeopleList.Children.Add(box);
        }
        HintText.Text = _choices.Count == 0
            ? "Добавить пока некого: здесь появляются компьютеры, которые были в сети."
            : _creating
                ? "Сообщения в группе получат все участники. Добавлять людей и переименовывать группу может любой из них."
                : "";
        HintText.Visibility = HintText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Validate();
    }

    private void Validate() =>
        OkButton.IsEnabled = EnteredName.Length > 0 && (!_creating || SelectedPeople.Count > 0);

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (OkButton != null) Validate();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (OkButton.IsEnabled) DialogResult = true;
    }
}
