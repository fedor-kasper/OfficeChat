using System.Net.Http.Json;
using System.Text.Json;
using OfficeChat.Protocol;

namespace OfficeChat.Server.Tests;

/// <summary>Перенос старой переписки (до аккаунтов): старые Id, сообщения, группы.</summary>
public class ImportTests
{
    private static async Task<LegacyClaimResponse> ClaimAsync(TestUser user, params Guid[] ids)
    {
        var response = await user.Http.PostAsJsonAsync("/api/legacy/claim", new LegacyClaimRequest(ids.ToList()), ServerProtocol.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LegacyClaimResponse>(ServerProtocol.Json))!;
    }

    private static async Task<ImportResponse> ImportAsync(TestUser user, List<MessageDto> messages, List<ImportedGroup>? groups = null)
    {
        var response = await user.Http.PostAsJsonAsync("/api/import", new ImportRequest(messages, groups ?? new()), ServerProtocol.Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ImportResponse>(ServerProtocol.Json))!;
    }

    private static MessageDto Old(Guid from, Guid? to, string text, Guid? group = null) => new()
    {
        Id = Guid.NewGuid(),
        SenderId = from,
        SenderName = "",
        RecipientId = to,
        GroupId = group,
        Text = text,
        SentAt = DateTimeOffset.UtcNow.AddDays(-30),
        ImportedState = 2,
    };

    [Fact]
    public async Task Legacy_id_belongs_to_one_account_and_is_announced()
    {
        await using var host = await TestHost.StartAsync();
        await using var alice = await host.RegisterAsync("alice");
        await using var boris = await host.RegisterAsync("boris");
        await boris.ConnectAsync();
        var aliceOldPc = Guid.NewGuid();

        var claim = await ClaimAsync(alice, aliceOldPc);
        Assert.Equal(new[] { aliceOldPc }, claim.Claimed);
        // Повтор — тот же результат; чужой старый Id не отдаём
        Assert.Equal(new[] { aliceOldPc }, (await ClaimAsync(alice, aliceOldPc)).Claimed);
        Assert.Equal(new[] { aliceOldPc }, (await ClaimAsync(boris, aliceOldPc)).Taken);

        // Остальные узнают сразу — и могут спросить список целиком
        var announced = await boris.WaitForAsync(f => f.Op == Ops.Legacy);
        var mapping = announced.Data!.Value.Deserialize<List<LegacyMapping>>(ServerProtocol.Json)!.Single();
        Assert.Equal(new LegacyMapping(aliceOldPc, alice.Id), mapping);
        var all = await boris.Http.GetFromJsonAsync<List<LegacyMapping>>("/api/legacy", ServerProtocol.Json);
        Assert.Contains(mapping, all!);
    }

    [Fact]
    public async Task Imported_history_reaches_own_devices_and_follows_colleague_who_moves_later()
    {
        await using var host = await TestHost.StartAsync();
        await using var alice = await host.RegisterAsync("alice");
        await using var alicePhone = await host.LoginAsync("alice");
        var aliceOldPc = Guid.NewGuid();
        var borisOldPc = Guid.NewGuid();
        await ClaimAsync(alice, aliceOldPc);

        // Старая переписка Алисы с Борисом, который ещё без аккаунта
        var mine = Old(alice.Id, borisOldPc, "привет из прошлого");
        var his = Old(borisOldPc, alice.Id, "и тебе");
        his.SenderName = "Борис";
        var result = await ImportAsync(alice, new() { mine, his });
        Assert.Equal(2, result.Messages);

        await alicePhone.ConnectAsync();
        var events = alicePhone.Events(EventKinds.Message)
            .Select(e => e.Data!.Value.Deserialize<MessageDto>(ServerProtocol.Json)!).ToList();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.True(e.Imported));
        Assert.Equal(borisOldPc, events.Single(e => e.Id == mine.Id).RecipientId);
        Assert.Equal("Борис", events.Single(e => e.Id == his.Id).SenderName);

        // Борис перешёл на аккаунт — старые сообщения теперь его: Алиса может их править, он видит правку
        await using var boris = await host.RegisterAsync("boris", "Борис");
        await ClaimAsync(boris, borisOldPc);
        await boris.ConnectAsync();
        await alice.ConnectAsync();
        await alice.OkAsync(Ops.Edit, new EditData(mine.Id, "привет из прошлого (исправлено)"));
        await boris.EventAsync(EventKinds.Edit, d => d.GetProperty("messageId").GetGuid() == mine.Id);

        // Чужое сообщение под своим именем не перенести
        var forged = Old(Guid.NewGuid(), Guid.NewGuid(), "не моё");
        Assert.Equal(0, (await ImportAsync(alice, new() { forged })).Messages);
    }

    [Fact]
    public async Task Imported_group_waits_for_members_without_accounts()
    {
        await using var host = await TestHost.StartAsync();
        await using var alice = await host.RegisterAsync("alice");
        await using var boris = await host.RegisterAsync("boris");
        var viktorOldPc = Guid.NewGuid();
        var group = new ImportedGroup(Guid.NewGuid(), "Бухгалтерия", new() { alice.Id, boris.Id, viktorOldPc });

        await alice.ConnectAsync();
        await boris.ConnectAsync();
        var result = await ImportAsync(alice, new() { Old(alice.Id, null, "старое в группе", group.Id) }, new() { group });
        Assert.Equal(1, result.Groups);
        Assert.Equal(1, result.Messages);
        var created = await boris.EventAsync(EventKinds.Group, d => d.GetProperty("id").GetGuid() == group.Id);
        Assert.Equal(GroupActions.Imported, created.Data!.Value.GetProperty("action").GetString());
        // Старые сообщения — только тому, кто их перенёс
        await boris.AssertNoFrameAsync(f => f.Kind == EventKinds.Message);

        // Борис переносит ту же группу — повтор, ничего не меняется
        Assert.Equal(0, (await ImportAsync(boris, new(), new() { group })).Groups);

        // Виктор перешёл на аккаунт и закрепил старый Id — он в группе
        await using var viktor = await host.RegisterAsync("viktor");
        await ClaimAsync(viktor, viktorOldPc);
        await viktor.ConnectAsync();
        await viktor.EventAsync(EventKinds.Group, d => d.GetProperty("id").GetGuid() == group.Id);
        Assert.Contains(viktor.Id, host.Db.GroupMembers(group.Id));
    }
}
