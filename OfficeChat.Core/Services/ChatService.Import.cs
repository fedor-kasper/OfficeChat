using System.IO;
using System.Net;
using OfficeChat.Client;
using OfficeChat.Models;
using OfficeChat.Protocol;

namespace OfficeChat.Services;

/// <summary>
/// Старая переписка (когда программа работала без аккаунтов): выгрузка на сервер после переноса и склейка
/// старых собеседников с их аккаунтами, когда они тоже переходят.
/// </summary>
public sealed partial class ChatService
{
    /// <summary>Состояние выгрузки старой переписки на сервер (в таблице state).</summary>
    public const string ImportStateKey = "legacy_import";
    public const string ImportPending = "pending";
    private const string ImportDone = "done";
    private const string ImportCursorKey = "legacy_import_cursor";
    private const string ImportGroupsKey = "legacy_import_groups";

    /// <summary>Вложения больше — не выгружаем (остаются только на этом компьютере).</summary>
    private const long MaxImportedAttachment = 25 * 1024 * 1024;

    // Тело запроса на сервере ограничено (1 МБ) — пачки держим заметно меньше.
    private const int ImportBatchBytes = 600 * 1024;
    private const int ImportPageSize = 200;

    /// <summary>Перенесённая переписка выгружена на сервер (пора выгрузить и напоминания).</summary>
    public event Action? LegacyHistoryImported;

    private bool _importing;
    private readonly HashSet<Guid> _appliedLegacy = new();

    /// <summary>Собеседник сообщения с сервера. У перенесённого — может быть ещё не перешедший на аккаунт.</summary>
    private Contact Counterpart(Guid id, string name, bool imported)
    {
        if (!imported || _serverAccounts.Contains(id) || _contactsByPeer.ContainsKey(id)) return ContactOfAccount(id, name);
        // Обычный собеседник «напрямую»: когда (если) перейдёт на аккаунт, склеится с ним.
        var peer = _discovery.Find(id) ?? _discovery.AddKnown(id, name, "", IPAddress.None);
        var contact = GetOrAddContact(peer);
        _store.SaveContact(peer);
        return contact;
    }

    private static MessageStatus ImportedStatus(int state) => state switch
    {
        2 => MessageStatus.Read,
        1 => MessageStatus.Delivered,
        _ => MessageStatus.Sent,
    };

    /// <summary>Чужое перенесённое сообщение (выгружено с другого нашего устройства): тихо, уже прочитанным, по времени.</summary>
    private async Task OnImportedMessageAsync(Contact conversation, MessageDto dto)
    {
        var kind = KindOf(dto);
        if (await StoreAttachmentAsync(dto, kind) is not { } filePath) return;
        var message = new ChatMessage
        {
            Id = dto.Id,
            IsOutgoing = false,
            Kind = kind,
            Text = dto.Text,
            FileName = FileStore.SafeFileName(dto.FileName),
            ImagePath = kind == MessageKind.Image ? ImageStore.PathFor(dto.Id, dto.FileName) : "",
            FilePath = filePath,
            FileSize = dto.FileSize,
            Timestamp = dto.SentAt.LocalDateTime,
            IsBroadcast = dto.Broadcast,
            IsQuiet = dto.Quiet,
            IsSticker = dto.Sticker,
            ReplyToId = dto.ReplyToId,
            ReplyAuthor = dto.ReplyToId is { } replyTo && IsMyMessage(conversation, replyTo) ? MyReplyAuthor : dto.ReplyAuthor,
            ReplyText = dto.ReplyText,
            SenderId = conversation.IsGroup ? dto.SenderId : null,
            SenderName = conversation.IsGroup ? dto.SenderName : "",
            IsRead = true,
            ReadReceiptSent = true,
            ServerFileId = dto.FileId,
        };
        InsertByTime(conversation, message);
        if (conversation.Peer != null) _store.SaveContact(conversation.Peer);
        Save(conversation, message);
    }

    // ---- Старые Id коллег → их аккаунты ----

    private async Task FetchLegacyMappingsAsync()
    {
        if (_session == null) return;
        try
        {
            ApplyLegacyMappings(await _session.Api.LegacyMappingsAsync());
        }
        catch (Exception ex) when (ex is HttpRequestException or ServerApiException or TaskCanceledException)
        {
            Log.Warn("Не удалось узнать, кто перенёс старую переписку", ex);
        }
    }

    private void ApplyLegacyMappings(List<LegacyMapping> mappings)
    {
        foreach (var mapping in mappings)
        {
            if (mapping.LegacyId == mapping.AccountId || !_appliedLegacy.Add(mapping.LegacyId)) continue;
            MergeLegacyPeer(mapping.LegacyId, mapping.AccountId);
        }
    }

    /// <summary>Коллега перешёл на аккаунт: его старая переписка «напрямую» — теперь переписка с аккаунтом.</summary>
    private void MergeLegacyPeer(Guid legacyId, Guid accountId)
    {
        var changed = _store.RemapPeer(legacyId, accountId);

        // Закрепы и «без уведомлений».
        foreach (var list in new[] { _settings.PinnedChats, _settings.MutedChats })
        {
            if (!list.Remove(legacyId)) continue;
            if (!list.Contains(accountId)) list.Add(accountId);
            changed = true;
        }

        // Группы, где он был под старым Id.
        foreach (var group in _groups.Values.Select(c => c.Group!).Where(g => g.HasMember(legacyId)))
        {
            group.Members = group.Members.Select(m => m.Id == legacyId ? m with { Id = accountId } : m)
                .DistinctBy(m => m.Id).ToList();
            changed = true;
        }

        if (_contactsByPeer.Remove(legacyId, out var old))
        {
            Contacts.Remove(old);
            _discovery.Forget(legacyId);
            if (accountId != MyId && old.Messages.Count > 0)
            {
                var target = ContactOfAccount(accountId, old.Title);
                foreach (var message in old.Messages.ToList())
                    if (target.Messages.All(m => m.Id != message.Id))
                        InsertByTime(target, message);
                target.UnreadCount += old.UnreadCount;
            }
            if (old.UnreadCount != 0) UnreadChanged?.Invoke();
            changed = true;
        }

        if (!changed) return;
        SettingsService.Save(_settings);
        UpdateGroupStatuses();
        Log.Info($"Старая переписка с {legacyId} теперь — с аккаунтом {accountId}");
    }

    // ---- Выгрузка перенесённой переписки на сервер ----

    /// <summary>
    /// После переноса старой переписки в аккаунт: выгрузить её на сервер пачками (группы, затем сообщения
    /// по времени). Обрыв связи не страшен — продолжим с того же места при следующем подключении.
    /// </summary>
    private async Task ImportLegacyHistoryAsync()
    {
        if (_importing || _session == null || _store.GetState(ImportStateKey) != ImportPending) return;
        _importing = true;
        try
        {
            var groups = _store.LoadGroups();
            var leftGroups = new HashSet<Guid>();
            if (_store.GetState(ImportGroupsKey) == null)
            {
                var imported = groups.Where(g => !_store.HasLeftGroup(g.Id))
                    .Select(g => new ImportedGroup(g.Id, g.Name, g.Members.Select(m => m.Id).ToList())).ToList();
                if (imported.Count > 0)
                    await _session.Api.ImportAsync(new ImportRequest(new List<MessageDto>(), imported));
                _store.SetState(ImportGroupsKey, "1");
                Log.Info($"Перенос на сервер: групп {imported.Count}");
            }
            var groupIds = groups.Select(g => g.Id).ToHashSet();

            var (after, afterId) = ReadImportCursor();
            var total = 0;
            while (IsServerConnected)
            {
                var page = _store.MessagesAfter(after, afterId, ImportPageSize);
                if (page.Count == 0)
                {
                    _store.SetState(ImportStateKey, ImportDone);
                    Log.Info("Перенос старой переписки на сервер завершён");
                    // Закрепы и «без уведомлений» из старой переписки — на все устройства.
                    SyncSetting(PinnedSetting, _settings.PinnedChats);
                    SyncSetting(MutedSetting, _settings.MutedChats);
                    LegacyHistoryImported?.Invoke();
                    break;
                }
                var batch = new List<MessageDto>();
                var bytes = 0;
                foreach (var (peerId, message) in page)
                {
                    var isGroup = groupIds.Contains(peerId);
                    // Из группы вышли — её старые сообщения никуда не переносим.
                    if (!isGroup && (leftGroups.Contains(peerId) || _store.HasLeftGroup(peerId)))
                        leftGroups.Add(peerId);
                    else if (await ToImportedAsync(peerId, isGroup, message) is { } dto)
                    {
                        batch.Add(dto);
                        bytes += 1024 + (dto.Text.Length + dto.ReplyText.Length) * 3;
                    }
                    (after, afterId) = (message.Timestamp, message.Id);
                    if (bytes > ImportBatchBytes) break;
                }
                if (batch.Count > 0)
                    await _session.Api.ImportAsync(new ImportRequest(batch, new List<ImportedGroup>()));
                total += batch.Count;
                _store.SetState(ImportCursorKey, $"{after.Ticks}|{afterId}");
            }
            if (total > 0) Log.Info($"Перенос на сервер: сообщений {total}");
        }
        catch (Exception ex) when (ex is HttpRequestException or ServerApiException or TaskCanceledException or IOException)
        {
            Log.Warn("Перенос старой переписки на сервер прервался — продолжим при следующем подключении", ex);
        }
        finally
        {
            _importing = false;
        }
    }

    private (DateTime After, Guid AfterId) ReadImportCursor()
    {
        var parts = _store.GetState(ImportCursorKey)?.Split('|');
        return parts is [var ticks, var id] && long.TryParse(ticks, out var t) && Guid.TryParse(id, out var g)
            ? (new DateTime(t), g)
            : (DateTime.MinValue, Guid.Empty);
    }

    private async Task<MessageDto?> ToImportedAsync(Guid peerId, bool isGroup, ChatMessage message)
    {
        var dto = new MessageDto
        {
            Id = message.Id,
            Kind = message.Kind switch { MessageKind.Image => MessageKinds.Image, MessageKind.File => MessageKinds.File, _ => MessageKinds.Text },
            Text = message.Text,
            FileName = message.FileName,
            ReplyToId = message.ReplyToId,
            ReplyAuthor = message.ReplyAuthor == MyReplyAuthor ? _settings.DisplayName : message.ReplyAuthor,
            ReplyText = message.ReplyText,
            Quiet = message.IsQuiet,
            Sticker = message.IsSticker,
            Broadcast = message.IsBroadcast,
            SentAt = new DateTimeOffset(message.Timestamp),
            Imported = true,
        };
        var name = _contactsByPeer.GetValueOrDefault(peerId)?.Title ?? "";
        if (isGroup)
        {
            dto.GroupId = peerId;
            dto.SenderId = message.IsOutgoing ? MyId : message.SenderId ?? Guid.Empty;
            dto.SenderName = message.IsOutgoing ? _settings.DisplayName : message.SenderName;
            if (dto.SenderId == Guid.Empty) return null;
        }
        else if (message.IsOutgoing)
        {
            dto.SenderId = MyId;
            dto.RecipientId = peerId;
            dto.RecipientName = name;
        }
        else
        {
            dto.SenderId = peerId;
            dto.SenderName = name;
            dto.RecipientId = MyId;
        }
        dto.ImportedState = message.IsOutgoing
            ? message.Status switch { MessageStatus.Read => 2, MessageStatus.Delivered => 1, _ => 0 }
            : message.IsRead ? 2 : 0;

        if (message.IsImage || message.IsFile)
        {
            if (message.ServerFileId is { } uploaded)
            {
                dto.FileId = uploaded;
            }
            else
            {
                var path = message.IsImage ? message.ImagePath : message.FilePath;
                var info = string.IsNullOrEmpty(path) ? null : new FileInfo(path);
                if (info is { Exists: true, Length: <= MaxImportedAttachment })
                {
                    var file = await _session!.Api.UploadAsync(path!, message.FileName.Length > 0 ? message.FileName : info.Name);
                    _store.SetServerFile(message.Id, file.Id);
                    dto.FileId = file.Id;
                }
            }
        }
        return dto;
    }
}
