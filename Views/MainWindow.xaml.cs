using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using OfficeChat.Models;
using OfficeChat.Services;

namespace OfficeChat.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly DiscoveryService _discovery;
    private readonly ObservableCollection<Contact> _contacts = new();

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;

        _contacts.Add(Contact.Everyone());
        ContactsList.ItemsSource = _contacts;
        MyNameText.Text = settings.DisplayName;

        _discovery = new DiscoveryService(settings);
        _discovery.PeerAdded += OnPeerAdded;
        _discovery.PeerRemoved += OnPeerRemoved;
        _discovery.Start();

        UpdateOnlineCount();
    }

    private void OnPeerAdded(Peer peer)
    {
        // Сортируем по имени, «Все» всегда остаётся первым.
        var index = 1;
        while (index < _contacts.Count &&
               string.Compare(_contacts[index].Title, peer.Name, StringComparison.CurrentCultureIgnoreCase) < 0)
            index++;
        _contacts.Insert(index, Contact.For(peer));
        UpdateOnlineCount();
    }

    private void OnPeerRemoved(Peer peer)
    {
        var contact = _contacts.FirstOrDefault(c => c.Peer?.Id == peer.Id);
        if (contact != null)
            _contacts.Remove(contact);
        UpdateOnlineCount();
    }

    private void UpdateOnlineCount()
    {
        var count = _contacts.Count - 1;
        OnlineCountText.Text = count == 0 ? "В СЕТИ НИКОГО НЕТ" : $"В СЕТИ: {count}";
    }

    private void ContactsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ContactsList.SelectedItem is not Contact contact)
        {
            ChatTitleText.Text = "Выберите, кому написать";
            ChatHintText.Text = "Слева — компьютеры, на которых сейчас запущен OfficeChat.";
            return;
        }

        ChatTitleText.Text = contact.IsEveryone ? "Общий чат" : contact.Title;
        ChatHintText.Text = "Отправка сообщений появится на следующем этапе.";
    }

    private void ChangeName_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NameWindow(_settings.DisplayName) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _settings.DisplayName = dialog.EnteredName;
        SettingsService.Save(_settings);
        MyNameText.Text = _settings.DisplayName;
        _discovery.AnnounceNow();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _discovery.Dispose();
        base.OnClosing(e);
    }
}
