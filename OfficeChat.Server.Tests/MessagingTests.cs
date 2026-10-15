using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using OfficeChat.Protocol;

namespace OfficeChat.Server.Tests;

public class MessagingTests
{
    private static Guid IdOf(JsonElement data, string property = "id") => data.GetProperty(property).GetGuid();

    private static bool HasMessage(JsonElement data, Guid id) => IdOf(data) == id;

    private static bool ListsMessage(JsonElement data, Guid id) =>
        data.GetProperty("messageIds").EnumerateArray().Any(x => x.GetGuid() == id);

    [Fact]
    public async Task Message_reaches_recipient_and_all_own_devices_with_delivered_and_read()
    {
        await using var host = await TestHost.StartAsync();
        await using var alice = await host.RegisterAsync("alice", "Алиса");
        await using var boris = await host.RegisterAsync("boris", "Борис");
        await using var alicePhone = await host.LoginAsync("alice");
        await alice.ConnectAsync();
        await boris.ConnectAsync();
        await alicePhone.ConnectAsync();

        var message = TestUser.Text(boris.Id, "Привет, Борис!");
        await alice.OkAsync(Ops.Send, message);

        var received = await boris.EventAsync(EventKinds.Message, d => HasMessage(d, message.Id));
        var dto = received.Data!.Value.Deserialize<MessageDto>(ServerProtocol.Json)!;
        Assert.Equal("Привет, Борис!", dto.Text);
        Assert.Equal(alice.Id, dto.SenderId);
        Assert.Equal("Алиса", dto.SenderName);
        // Своё сообщение видно и на телефоне Алисы
        await alicePhone.EventAsync(EventKinds.Message, d => HasMessage(d, message.Id));

        // Борис подтвердил получение → у Алисы «доставлено»
        await boris.SendFrameAsync(new Frame { Op = Ops.Ack, Seq = received.Seq });
        var delivered = await alice.EventAsync(EventKinds.Delivered, d => ListsMessage(d, message.Id));
        Assert.Equal(boris.Id, IdOf(delivered.Data!.Value, "accountId"));

        // Борис прочитал → у Алисы «прочитано»
        await boris.OkAsync(Ops.Read, new MessageIdsData(new List<Guid> { message.Id }));
        await alice.EventAsync(EventKinds.Read, d => ListsMessage(d, message.Id));

        // Повтор отправки с тем же Id не дублирует сообщение
        await alice.OkAsync(Ops.Send, message);
        await boris.AssertNoFrameAsync(f => f.Kind == EventKinds.Message && f.Seq > received.Seq);
    }

    [Fact]
    public async Task Offline_recipient_and_new_device_get_full_history()
    {
        await using var host = await TestHost.StartAsync();
        await using var alice = await host.RegisterAsync("alice");
        var boris = await host.RegisterAsync("boris");
        await alice.ConnectAsync();

        var first = TestUser.Text(boris.Id, "первое");
        var second = TestUser.Text(boris.Id, "второе");
        await alice.OkAsync(Ops.Send, first);
        await alice.OkAsync(Ops.Send, second);
        await alice.OkAsync(Ops.Edit, new EditData(second.Id, "второе (исправлено)"));

        // Борис включился позже — получает всё по порядку
        await boris.ConnectAsync();
        var messages = boris.Events(EventKinds.Message).ToList();
        Assert.Equal(2, messages.Count);
        Assert.Equal(first.Id, IdOf(messages[0].Data!.Value));
        Assert.Single(boris.Events(EventKinds.Edit));

        // Продолжение с последнего номера — без повторов
        var lastSeq = boris.Received.Where(f => f.Seq != null).Max(f => f.Seq!.Value);
        await boris.DisconnectAsync();
        await alice.OkAsync(Ops.Send, TestUser.Text(boris.Id, "третье"));
        await boris.ConnectAsync(lastSeq);
        Assert.Single(boris.Events(EventKinds.Message));

        // Новый телефон Бориса получает всю историю
        await using var phone = await host.LoginAsync("boris");
        await phone.ConnectAsync();
        Assert.Equal(3, phone.Events(EventKinds.Message).Count());
    }

    [Fact]
    public async Task Edit_and_delete_only_own_messages()
    {
        await using var host = await TestHost.StartAsync();
        await using var alice = await host.RegisterAsync("alice");
        await using var boris = await host.RegisterAsync("boris");
        await alice.ConnectAsync();
        await boris.ConnectAsync();

        var message = TestUser.Text(boris.Id, "исходный");
        await alice.OkAsync(Ops.Send, message);
        await boris.EventAsync(EventKinds.Message, d => HasMessage(d, message.Id));

        var foreignEdit = await boris.RequestAsync(Ops.Edit, new EditData(message.Id, "взлом"));
        Assert.Equal(ErrorCodes.Forbidden, foreignEdit.Error);

        await alice.OkAsync(Ops.Edit, new EditData(message.Id, "исправленный"));
        var edit = await boris.EventAsync(EventKinds.Edit);
        Assert.Equal("исправленный", edit.Data!.Value.GetProperty("text").GetString());

        await alice.OkAsync(Ops.Delete, new DeleteData(message.Id));
        await boris.EventAsync(EventKinds.Delete, d => IdOf(d, "messageId") == message.Id);
        var again = await alice.RequestAsync(Ops.Edit, new EditData(message.Id, "после удаления"));
        Assert.Equal(ErrorCodes.NotFound, again.Error);
    }

    [Fact]
    public async Task Bad_messages_are_rejected()
    {
        await using var host = await TestHost.StartAsync();
        await using var alice = await host.RegisterAsync("alice");
        await alice.ConnectAsync();

        Assert.Equal(ErrorCodes.NotFound, (await alice.RequestAsync(Ops.Send, TestUser.Text(Guid.NewGuid(), "кому?"))).Error);
        Assert.Equal(ErrorCodes.BadRequest, (await alice.RequestAsync(Ops.Send, TestUser.Text(alice.Id, "себе"))).Error);
        var empty = TestUser.Text(Guid.NewGuid(), " ");
        Assert.Equal(ErrorCodes.BadRequest, (await alice.RequestAsync(Ops.Send, empty)).Error);
        var image = TestUser.Text(Guid.NewGuid(), "");
        image.Kind = MessageKinds.Image;
        image.FileId = Guid.NewGuid();
        Assert.NotEqual(Ops.Ok, (await alice.RequestAsync(Ops.Send, image)).Op);
    }

    [Fact]
    public async Task Groups_create_message_add_and_leave()
    {
        await using var host = await TestHost.StartAsync();
        await using var alice = await host.RegisterAsync("alice", "Алиса");
        await using var boris = await host.RegisterAsync("boris", "Борис");
        await using var viktor = await host.RegisterAsync("viktor", "Виктор");
        await using var gleb = await host.RegisterAsync("gleb", "Глеб");
        foreach (var u in new[] { alice, boris, viktor, gleb }) await u.ConnectAsync();

        var groupId = Guid.NewGuid();
        await alice.OkAsync(Ops.GroupCreate, new GroupCreateData(groupId, "Бухгалтерия", new List<Guid> { boris.Id, viktor.Id }));
        var created = await viktor.EventAsync(EventKinds.Group, d => IdOf(d) == groupId);
        var group = created.Data!.Value.Deserialize<GroupDto>(ServerProtocol.Json)!;
        Assert.Equal(GroupActions.Created, group.Action);
        Assert.Equal(3, group.Members.Count);

        var hello = TestUser.GroupText(groupId, "Всем привет");
        await boris.OkAsync(Ops.Send, hello);
        await alice.EventAsync(EventKinds.Message, d => HasMessage(d, hello.Id));
        await viktor.EventAsync(EventKinds.Message, d => HasMessage(d, hello.Id));
        await gleb.AssertNoFrameAsync(f => f.Kind == EventKinds.Message);

        // Не участник писать в группу не может
        Assert.Equal(ErrorCodes.Forbidden, (await gleb.RequestAsync(Ops.Send, TestUser.GroupText(groupId, "я тут"))).Error);

        // Любой участник добавляет и переименовывает
        await viktor.OkAsync(Ops.GroupAdd, new GroupAddData(groupId, new List<Guid> { gleb.Id }));
        var added = await gleb.EventAsync(EventKinds.Group, d => IdOf(d) == groupId);
        Assert.Equal(4, added.Data!.Value.GetProperty("members").GetArrayLength());
        await gleb.OkAsync(Ops.GroupRename, new GroupRenameData(groupId, "Отдел кадров"));
        await alice.EventAsync(EventKinds.Group, d => d.GetProperty("name").GetString() == "Отдел кадров");

        // Виктор вышел — следующие сообщения ему не приходят
        await viktor.OkAsync(Ops.GroupLeave, new GroupIdData(groupId));
        await alice.EventAsync(EventKinds.Group, d => d.GetProperty("action").GetString() == GroupActions.Left);
        var after = TestUser.GroupText(groupId, "без Виктора");
        await alice.OkAsync(Ops.Send, after);
        await gleb.EventAsync(EventKinds.Message, d => HasMessage(d, after.Id));
        await viktor.AssertNoFrameAsync(f => f.Kind == EventKinds.Message && HasMessage(f.Data!.Value, after.Id));

        // Прочтение в группе: автор видит, кто прочитал
        await gleb.OkAsync(Ops.Read, new MessageIdsData(new List<Guid> { after.Id }));
        var read = await alice.EventAsync(EventKinds.Read, d => ListsMessage(d, after.Id));
        Assert.Equal(gleb.Id, IdOf(read.Data!.Value, "accountId"));
    }

    [Fact]
    public async Task Files_upload_send_and_download_only_by_participants()
    {
        await using var host = await TestHost.StartAsync();
        await using var alice = await host.RegisterAsync("alice");
        await using var boris = await host.RegisterAsync("boris");
        var stranger = await host.RegisterAsync("stranger");
        await alice.ConnectAsync();
        await boris.ConnectAsync();

        var content = new byte[300_000];
        Random.Shared.NextBytes(content);
        var upload = await alice.Http.PostAsync("/api/files?name=" + Uri.EscapeDataString("отчёт.pdf"), new ByteArrayContent(content));
        upload.EnsureSuccessStatusCode();
        var file = (await upload.Content.ReadFromJsonAsync<FileUploadResponse>(ServerProtocol.Json))!;
        Assert.Equal(content.Length, file.Size);

        var message = new MessageDto
        {
            Id = Guid.NewGuid(), RecipientId = boris.Id, Kind = MessageKinds.File, FileId = file.Id, Text = "держи",
        };
        await alice.OkAsync(Ops.Send, message);
        var received = await boris.EventAsync(EventKinds.Message, d => HasMessage(d, message.Id));
        var dto = received.Data!.Value.Deserialize<MessageDto>(ServerProtocol.Json)!;
        Assert.Equal("отчёт.pdf", dto.FileName);
        Assert.Equal(content.Length, dto.FileSize);

        var downloaded = await boris.Http.GetByteArrayAsync($"/api/files/{file.Id}");
        Assert.Equal(content, downloaded);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"/api/files/{file.Id}")).StatusCode);

        // Чужой файл к своему сообщению не приложить
        var borrowed = new MessageDto { Id = Guid.NewGuid(), RecipientId = alice.Id, Kind = MessageKinds.File, FileId = file.Id };
        Assert.Equal(ErrorCodes.BadRequest, (await boris.RequestAsync(Ops.Send, borrowed)).Error);
    }

    [Fact]
    public async Task Reminders_are_shared_with_participants()
    {
        await using var host = await TestHost.StartAsync();
        await using var alice = await host.RegisterAsync("alice");
        await using var boris = await host.RegisterAsync("boris");
        await using var viktor = await host.RegisterAsync("viktor");
        foreach (var u in new[] { alice, boris, viktor }) await u.ConnectAsync();

        var id = Guid.NewGuid();
        await alice.OkAsync(Ops.ReminderPut, new ReminderPutData(id, new List<Guid> { boris.Id, viktor.Id }, "{\"text\":\"Отчёт\"}"));
        var shared = await boris.EventAsync(EventKinds.Reminder, d => IdOf(d) == id);
        Assert.Equal("{\"text\":\"Отчёт\"}", shared.Data!.Value.GetProperty("json").GetString());

        // Чужое напоминание менять нельзя
        Assert.Equal(ErrorCodes.Forbidden,
            (await boris.RequestAsync(Ops.ReminderPut, new ReminderPutData(id, new List<Guid>(), "{}"))).Error);

        await boris.OkAsync(Ops.ReminderDone, new ReminderDoneData(id, true));
        var done = await alice.EventAsync(EventKinds.ReminderDone, d => IdOf(d) == id);
        Assert.Equal(boris.Id, IdOf(done.Data!.Value, "accountId"));

        // Виктора убрали из участников — у него напоминание пропадает
        await alice.OkAsync(Ops.ReminderPut, new ReminderPutData(id, new List<Guid> { boris.Id }, "{\"text\":\"Отчёт!\"}"));
        await viktor.EventAsync(EventKinds.ReminderDelete, d => IdOf(d) == id);

        await alice.OkAsync(Ops.ReminderDelete, new ReminderIdData(id));
        await boris.EventAsync(EventKinds.ReminderDelete, d => IdOf(d) == id);
    }

    [Fact]
    public async Task Settings_sync_between_own_devices()
    {
        await using var host = await TestHost.StartAsync();
        await using var pc = await host.RegisterAsync("alice");
        await using var phone = await host.LoginAsync("alice");
        await pc.ConnectAsync();
        await phone.ConnectAsync();
        await pc.OkAsync(Ops.SettingsPut, new SettingsData("pinned", "[\"a\"]"));
        var setting = await phone.EventAsync(EventKinds.Settings);
        Assert.Equal("[\"a\"]", setting.Data!.Value.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Signals_and_presence_reach_online_devices_only()
    {
        await using var host = await TestHost.StartAsync();
        await using var alice = await host.RegisterAsync("alice", "Алиса");
        await using var boris = await host.RegisterAsync("boris");
        await boris.ConnectAsync();
        await alice.ConnectAsync();

        // Борис видит, что Алиса появилась в сети
        await boris.WaitForAsync(f => f.Op == Ops.Presence && f.AccountId == alice.Id && f.Online == true);

        await alice.OkAsync(Ops.Signal, new SignalData(boris.Id, null, "typing", null));
        var typing = await boris.WaitForAsync(f => f.Op == Ops.Signal);
        Assert.Equal("typing", typing.Type);
        Assert.Equal("Алиса", typing.FromName);
        Assert.Equal(alice.Id, typing.From);

        await alice.DisconnectAsync();
        await boris.WaitForAsync(f => f.Op == Ops.Presence && f.AccountId == alice.Id && f.Online == false);
        var directory = await boris.Http.GetFromJsonAsync<List<DirectoryEntry>>("/api/accounts", ServerProtocol.Json);
        Assert.Contains(directory!, a => a.Id == alice.Id && !a.Online);
    }

    [Fact]
    public async Task Revoked_device_is_disconnected()
    {
        await using var host = await TestHost.StartAsync();
        await using var pc = await host.RegisterAsync("alice");
        await using var phone = await host.LoginAsync("alice");
        await phone.ConnectAsync();
        await pc.Http.DeleteAsync($"/api/devices/{phone.Auth.DeviceId}");
        await Assert.ThrowsAsync<InvalidOperationException>(() => phone.WaitForAsync(_ => false));
    }
}
