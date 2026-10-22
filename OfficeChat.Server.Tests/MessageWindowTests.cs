using OfficeChat.Models;

namespace OfficeChat.Server.Tests;

/// <summary>Окно сообщений: при открытии — только последние, остальные — по «Показать раньше».</summary>
public class MessageWindowTests
{
    private static ChatMessage Message(int n) => new() { Id = Guid.NewGuid(), IsOutgoing = false, Text = n.ToString(), Timestamp = new DateTime(2026, 1, 1).AddMinutes(n) };

    private static Contact WithMessages(int count)
    {
        var contact = Contact.For(new Peer { Id = Guid.NewGuid(), Name = "Борис" });
        for (var i = 0; i < count; i++) contact.Messages.Add(Message(i));
        return contact;
    }

    [Fact]
    public void Shows_only_the_latest_and_follows_new_and_deleted()
    {
        var contact = WithMessages(200);
        using var window = new MessageWindow(contact);
        Assert.Equal(MessageWindow.InitialCount, window.Items.Count);
        Assert.Equal("199", window.Items[^1].Text);
        Assert.True(window.HasMore);

        contact.Messages.Add(Message(500));
        Assert.Equal("500", window.Items[^1].Text);

        var shown = window.Items[5];
        contact.Messages.Remove(shown);
        Assert.DoesNotContain(shown, window.Items);

        // Старое сообщение (по времени — в скрытой части) на экран не попадает
        contact.Messages.Insert(3, Message(-1));
        Assert.Equal(MessageWindow.InitialCount, window.Items.Count);
        Assert.Equal(contact.Messages.Skip(contact.Messages.Count - window.Items.Count), window.Items);
    }

    [Fact]
    public void Reveal_and_show_more_keep_order()
    {
        var contact = WithMessages(150);
        using var window = new MessageWindow(contact);
        Assert.True(window.Reveal(contact.Messages[100]));
        Assert.Same(contact.Messages[100], window.Items[0]);

        // Подгрузка из истории: вставки в начало — сначала в скрытую часть
        contact.HasOlderMessages = true;
        for (var i = 0; i < 3; i++) contact.Messages.Insert(i, Message(-10 + i));
        Assert.Same(contact.Messages[103], window.Items[0]);
        Assert.Equal(contact.Messages.Skip(103), window.Items);
    }

    [Fact]
    public void Empty_conversation_shows_new_messages()
    {
        var contact = WithMessages(0);
        using var window = new MessageWindow(contact);
        Assert.False(window.HasMore);
        contact.Messages.Add(Message(1));
        contact.Messages.Add(Message(2));
        Assert.Equal(new[] { "1", "2" }, window.Items.Select(m => m.Text));
    }
}
