using System.Text.Json;
using OfficeChat.Protocol;
using OfficeChat.Server.Data;

namespace OfficeChat.Server.Realtime;

/// <summary>
/// Перенос старой переписки (когда программа работала без аккаунтов и у каждого компьютера был свой Id).
/// Сотрудник закрепляет за аккаунтом свои старые Id и присылает историю пачками; она ложится в журнал
/// его аккаунта — и появляется на всех его устройствах. Коллеги, ещё не перешедшие на аккаунты, получают
/// перенесённые группы и сообщения, когда перейдут.
/// </summary>
public sealed partial class ChatLogic
{
    /// <summary>Сколько сообщений можно прислать за раз.</summary>
    public const int MaxImportBatch = 500;

    private const int MaxLegacyIds = 50;

    public async Task<LegacyClaimResponse> ClaimLegacyAsync(Guid accountId, List<Guid> legacyIds)
    {
        var claimed = new List<Guid>();
        var taken = new List<Guid>();
        var affected = new HashSet<Guid>();
        await db.WriteLock.WaitAsync();
        try
        {
            foreach (var legacyId in legacyIds.Distinct().Take(MaxLegacyIds))
            {
                if (legacyId == accountId || legacyId == Guid.Empty) continue;
                if (!db.ClaimLegacy(accountId, legacyId))
                {
                    taken.Add(legacyId);
                    continue;
                }
                claimed.Add(legacyId);
                // Перенесённые коллегами группы, где он был участником, — теперь и его.
                foreach (var groupId in db.TakePendingGroups(legacyId))
                {
                    if (db.FindGroup(groupId) == null || db.IsGroupMember(groupId, accountId)) continue;
                    db.AddGroupMember(groupId, accountId);
                    var members = db.GroupMembers(groupId);
                    db.AppendEvents(members, EventKinds.Group,
                        GroupEvent(groupId, GroupActions.Imported, accountId, new List<Guid> { accountId }));
                    affected.UnionWith(members);
                }
            }
        }
        finally
        {
            db.WriteLock.Release();
        }
        if (affected.Count > 0) hub.NotifyEvents(affected);
        if (claimed.Count > 0)
        {
            log.LogInformation("Аккаунт {Account}: перенесены старые Id {Ids}", accountId, string.Join(", ", claimed));
            var mappings = claimed.Select(id => new LegacyMapping(id, accountId)).ToList();
            hub.Broadcast(new Frame { Op = Ops.Legacy, Data = JsonSerializer.SerializeToElement(mappings, ServerProtocol.Json) });
        }
        return new LegacyClaimResponse(claimed, taken);
    }

    public async Task<ImportResponse> ImportAsync(Guid accountId, ImportRequest request)
    {
        if (request.Messages.Count > MaxImportBatch) throw Bad($"Не больше {MaxImportBatch} сообщений за раз");
        var groups = 0;
        var messages = 0;
        var affected = new HashSet<Guid>();
        await db.WriteLock.WaitAsync();
        try
        {
            foreach (var group in request.Groups.Take(MaxImportBatch))
                if (ImportGroup(accountId, group, affected)) groups++;
            foreach (var message in request.Messages)
                if (ImportMessage(accountId, message)) messages++;
            if (messages > 0) affected.Add(accountId);
        }
        finally
        {
            db.WriteLock.Release();
        }
        if (affected.Count > 0) hub.NotifyEvents(affected);
        return new ImportResponse(messages, groups);
    }

    private bool ImportGroup(Guid accountId, ImportedGroup imported, HashSet<Guid> affected)
    {
        if (imported.Id == Guid.Empty) return false;
        var accounts = new HashSet<Guid> { accountId };
        var pending = new List<Guid>();
        foreach (var id in imported.MemberIds.Distinct().Take(500))
        {
            if (db.ResolveAccount(id) is { } account) accounts.Add(account);
            else pending.Add(id);
        }

        if (db.FindGroup(imported.Id) is { } existing)
        {
            // Группу уже перенёс кто-то из участников — добавляем себя (если его старый Id там ждали или он сам из неё).
            if (existing.Members.Contains(accountId)) return false;
            db.AddGroupMember(existing.Id, accountId);
            var members = db.GroupMembers(existing.Id);
            db.AppendEvents(members, EventKinds.Group,
                GroupEvent(existing.Id, GroupActions.Imported, accountId, new List<Guid> { accountId }));
            affected.UnionWith(members);
            return true;
        }

        var name = imported.Name.Trim() is { Length: > 0 } n ? n : "Группа";
        if (name.Length > ServerProtocol.MaxNameLength) name = name[..ServerProtocol.MaxNameLength];
        if (!db.CreateGroup(imported.Id, name, accountId, accounts)) return false;
        foreach (var legacyId in pending) db.AddPendingGroupMember(imported.Id, legacyId);
        log.LogInformation("Перенесена группа «{Name}»: аккаунтов {Accounts}, ещё без аккаунта {Pending}",
            name, accounts.Count, pending.Count);
        db.AppendEvents(accounts, EventKinds.Group, GroupEvent(imported.Id, GroupActions.Imported, accountId));
        affected.UnionWith(accounts);
        return true;
    }

    private bool ImportMessage(Guid accountId, MessageDto message)
    {
        if (message.Id == Guid.Empty) return false;
        if (message.Kind is not (MessageKinds.Text or MessageKinds.Image or MessageKinds.File)) return false;
        if (message.Text.Length > ServerProtocol.MaxTextLength) message.Text = message.Text[..ServerProtocol.MaxTextLength];

        // Собеседник — по аккаунту, если уже перешёл; иначе остаётся старый Id (перепишется, когда перейдёт).
        Guid Resolve(Guid id) => db.ResolveAccount(id) ?? id;
        var sender = Resolve(message.SenderId);
        if (message.GroupId is { } groupId)
        {
            if (!db.IsGroupMember(groupId, accountId)) return false;
            message.RecipientId = null;
        }
        else if (message.RecipientId is { } recipient)
        {
            recipient = Resolve(recipient);
            if (sender != accountId && recipient != accountId) return false;
            if (sender == recipient) return false;
            message.RecipientId = recipient;
        }
        else
        {
            return false;
        }
        message.SenderId = sender;

        if (message.Kind != MessageKinds.Text)
        {
            // Вложение загружено им самим; не загрузилось (слишком большое, файла нет) — остаётся только подпись.
            if (message.FileId is { } fileId && db.FindFile(fileId) is { } file && file.OwnerId == accountId)
                message.FileSize = file.Size;
            else
                message.FileId = null;
        }
        else
        {
            message.FileId = null;
        }

        message.Imported = true;
        message.ImportedState = Math.Clamp(message.ImportedState, 0, 2);
        message.ServerTime = DateTimeOffset.UtcNow;
        if (message.SentAt == default) message.SentAt = message.ServerTime;
        if (string.IsNullOrEmpty(message.SenderName)) message.SenderName = NameOf(sender);

        // Перенёс и собеседник — сообщение уже есть; в журнал этого аккаунта всё равно кладём:
        // у каждого своя копия старой переписки.
        db.AddMessage(message);
        db.AppendEvents(new[] { accountId }, EventKinds.Message, message, message.Id, message.SenderId);
        return true;
    }
}
