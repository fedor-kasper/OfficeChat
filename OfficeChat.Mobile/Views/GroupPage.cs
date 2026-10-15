using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using OfficeChat.Models;

namespace OfficeChat.Mobile.Views;

/// <summary>Новая группа (название и участники) или состав существующей: переименовать, добавить людей.</summary>
public sealed class GroupPage : Page
{
    private readonly Contact? _group;
    private readonly TextBox _nameBox = new() { MaxLength = 64, Watermark = "Название группы" };
    private readonly StackPanel _people = new() { Spacing = 2 };
    private readonly List<(CheckBox Box, Contact Person)> _checks = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, IsVisible = false };

    public GroupPage(Contact? group)
    {
        _group = group;
        var body = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 6 };
        body.Children.Add(new TextBlock { Text = "Название", Classes = { "label" } });
        body.Children.Add(_nameBox);
        if (group != null)
        {
            _nameBox.Text = group.Title;
            body.Children.Add(new TextBlock { Text = "Участники", Classes = { "label" } });
            body.Children.Add(new TextBlock
            {
                Text = string.Join("\n", group.Group!.Members.Select(m => m.Id == MobileSession.Chat?.MyId ? $"{m.Name} (вы)" : m.Name)),
                TextWrapping = TextWrapping.Wrap, FontSize = 15,
            });
            body.Children.Add(new TextBlock { Text = "Добавить", Classes = { "label" } });
        }
        else
        {
            body.Children.Add(new TextBlock { Text = "Кого позвать", Classes = { "label" } });
        }
        body.Children.Add(_people);
        body.Children.Add(_error);
        var save = new Button { Content = group == null ? "Создать группу" : "Сохранить", Classes = { "primary" },
            Margin = new Avalonia.Thickness(0, 16, 0, 0) };
        save.Click += (_, _) => Save();
        body.Children.Add(save);
        Content = PageHeader.Wrap(this, group == null ? "Новая группа" : group.Title, new ScrollViewer { Content = body });
    }

    public override void OnAppearing()
    {
        _people.Children.Clear();
        _checks.Clear();
        var chat = MobileSession.Chat;
        if (chat == null) return;
        var candidates = chat.Contacts.Where(c => c.IsPerson && (_group == null || !_group.Group!.HasMember(c.Key)))
            .OrderByDescending(c => c.IsOnline).ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase);
        foreach (var person in candidates)
        {
            var box = new CheckBox
            {
                Content = person.Title + (person.IsOnline ? " · в сети" : ""), FontSize = 16,
                Padding = new Avalonia.Thickness(6, 8), HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            _checks.Add((box, person));
            _people.Children.Add(box);
        }
        if (_checks.Count == 0)
            _people.Children.Add(new TextBlock
            {
                Text = _group == null ? "Пока некого позвать." : "Все коллеги уже в группе.",
                Foreground = PageHeader.Resource("MutedTextBrush"),
            });
    }

    private void Save()
    {
        if (MobileSession.Chat is not { } chat) return;
        var name = _nameBox.Text?.Trim() ?? "";
        var chosen = _checks.Where(c => c.Box.IsChecked == true).Select(c => c.Person).ToList();
        if (name.Length == 0)
        {
            ShowError("Укажите название группы");
            return;
        }
        if (_group == null)
        {
            if (chosen.Count == 0)
            {
                ShowError("Выберите хотя бы одного участника");
                return;
            }
            var created = chat.CreateGroup(name, chosen);
            Shell.Pop();
            Shell.Push(new ConversationPage(created));
            return;
        }
        if (name != _group.Title) chat.RenameGroup(_group, name);
        if (chosen.Count > 0) chat.AddGroupMembers(_group, chosen);
        Shell.Pop();
    }

    private void ShowError(string text)
    {
        _error.Text = text;
        _error.IsVisible = true;
    }
}
