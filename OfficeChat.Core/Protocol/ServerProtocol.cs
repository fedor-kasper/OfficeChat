using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace OfficeChat.Protocol;

/// <summary>
/// Протокол сервера OfficeChat — общий для сервера и всех клиентов. Описание: docs/SERVER-PROTOCOL.md.
/// </summary>
public static class ServerProtocol
{
    /// <summary>Версия протокола: растёт при несовместимых изменениях.</summary>
    public const int Version = 1;

    public const string AppTag = "OfficeChat/1";

    /// <summary>HTTPS и WebSocket.</summary>
    public const int DefaultPort = 45680;

    /// <summary>UDP: поиск сервера в локальной сети.</summary>
    public const int DiscoveryPort = 45681;

    public const int MaxTextLength = 4000;
    public const int MaxNameLength = 64;
    public const int MinPasswordLength = 6;
    public const long MaxFileSize = 1024L * 1024 * 1024;

    /// <summary>JSON в стиле camelCase, как в вебе; русский текст — без \u-экранирования.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };
}

/// <summary>Коды ошибок (поле error) — по ним клиент решает, что делать; message — текст для человека.</summary>
public static class ErrorCodes
{
    public const string BadRequest = "bad-request";
    public const string LoginTaken = "login-taken";
    public const string BadCredentials = "bad-credentials";
    public const string Blocked = "blocked";
    public const string TooManyAttempts = "too-many-attempts";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string NotFound = "not-found";
}

/// <summary>Типы событий журнала (поле kind).</summary>
public static class EventKinds
{
    public const string Message = "message";
    public const string Edit = "edit";
    public const string Delete = "delete";
    public const string Delivered = "delivered";
    public const string Read = "read";
    public const string Group = "group";
    public const string Reminder = "reminder";
    public const string ReminderDelete = "reminder-delete";
    public const string ReminderDone = "reminder-done";
    public const string Settings = "settings";
}

/// <summary>Операции в WebSocket (поле op).</summary>
public static class Ops
{
    // Клиент → сервер
    public const string Sync = "sync";
    public const string Ack = "ack";
    public const string Send = "send";
    public const string Edit = "edit";
    public const string Delete = "delete";
    public const string Read = "read";
    public const string GroupCreate = "group.create";
    public const string GroupRename = "group.rename";
    public const string GroupAdd = "group.add";
    public const string GroupLeave = "group.leave";
    public const string ReminderPut = "reminder.put";
    public const string ReminderDelete = "reminder.delete";
    public const string ReminderDone = "reminder.done";
    public const string SettingsPut = "settings.put";
    public const string Signal = "signal";
    public const string Ping = "ping";

    // Сервер → клиент
    public const string Event = "event";
    public const string Synced = "synced";
    public const string Ok = "ok";
    public const string Error = "error";
    public const string Presence = "presence";
    public const string Directory = "directory";
    public const string Pong = "pong";
}

public static class MessageKinds
{
    public const string Text = "text";
    public const string Image = "image";
    public const string File = "file";
}

public static class GroupActions
{
    public const string Created = "created";
    public const string Renamed = "renamed";
    public const string Added = "added";
    public const string Left = "left";
}

// ---- HTTP ----

public sealed record DeviceInfo(string Name, string Platform);

public sealed record RegisterRequest(string Login, string DisplayName, string Password, DeviceInfo Device);

public sealed record LoginRequest(string Login, string Password, DeviceInfo Device);

public sealed record AccountDto(Guid Id, string Login, string DisplayName, bool IsAdmin, bool MustChangePassword);

public sealed record AuthResponse(string Token, Guid DeviceId, AccountDto Account);

public sealed record ErrorResponse(string Error, string Message);

public sealed record ChangePasswordRequest(string OldPassword, string NewPassword);

public sealed record ProfileRequest(string DisplayName);

public sealed record DirectoryEntry(Guid Id, string Login, string DisplayName, bool Online, DateTimeOffset? LastSeen);

public sealed record DeviceDto(Guid Id, string Name, string Platform, DateTimeOffset CreatedAt, DateTimeOffset? LastSeen,
    bool Current);

public sealed record ServerInfo(string Name, string Version, int Protocol);

public sealed record FileUploadResponse(Guid Id, long Size);

public sealed record AdminAccountDto(Guid Id, string Login, string DisplayName, bool IsAdmin, bool IsBlocked,
    DateTimeOffset CreatedAt, DateTimeOffset? LastSeen, int Devices, bool Online);

public sealed record ResetPasswordResponse(string TemporaryPassword);

public sealed record BlockRequest(bool Blocked);

public sealed record AdminRequest(bool IsAdmin);

/// <summary>Ответ сервера на поиск в сети (UDP).</summary>
public sealed record ServerAnnouncement(string App, string Type, string Name, int Port, string Fingerprint, string Version);

// ---- WebSocket ----

/// <summary>Кадр WebSocket. Какие поля заполнены — зависит от op.</summary>
public sealed class Frame
{
    public string Op { get; set; } = "";
    public string? Rid { get; set; }
    public long? Seq { get; set; }
    public long? Since { get; set; }
    public string? Kind { get; set; }
    public JsonElement? Data { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }

    // presence / signal / directory
    public Guid? AccountId { get; set; }
    public bool? Online { get; set; }
    public Guid? From { get; set; }
    public string? FromName { get; set; }
    public Guid? GroupId { get; set; }
    public string? Type { get; set; }
    public JsonElement? Payload { get; set; }
    public DirectoryEntry? Account { get; set; }
}

/// <summary>Сообщение: личное (RecipientId) или в группе (GroupId).</summary>
public sealed class MessageDto
{
    public Guid Id { get; set; }
    /// <summary>Заполняет сервер.</summary>
    public Guid SenderId { get; set; }
    /// <summary>Заполняет сервер.</summary>
    public string SenderName { get; set; } = "";
    public Guid? RecipientId { get; set; }
    public Guid? GroupId { get; set; }
    public string Kind { get; set; } = MessageKinds.Text;
    public string Text { get; set; } = "";
    public Guid? FileId { get; set; }
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public Guid? ReplyToId { get; set; }
    public string ReplyAuthor { get; set; } = "";
    public string ReplyText { get; set; } = "";
    public bool Quiet { get; set; }
    public bool Sticker { get; set; }
    public bool Broadcast { get; set; }
    /// <summary>Время отправки по часам автора.</summary>
    public DateTimeOffset SentAt { get; set; }
    /// <summary>Когда сервер принял сообщение — заполняет сервер.</summary>
    public DateTimeOffset ServerTime { get; set; }
}

public sealed record EditData(Guid MessageId, string Text, DateTimeOffset? EditedAt = null, Guid? SenderId = null);

public sealed record DeleteData(Guid MessageId, Guid? SenderId = null);

public sealed record MessageIdsData(List<Guid> MessageIds, Guid? AccountId = null);

public sealed record GroupMemberDto(Guid Id, string DisplayName);

public sealed class GroupDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public List<GroupMemberDto> Members { get; set; } = new();
    /// <summary>Что произошло: created / renamed / added / left.</summary>
    public string Action { get; set; } = "";
    public Guid ActorId { get; set; }
    public string ActorName { get; set; } = "";
    /// <summary>Кого добавили (для added).</summary>
    public List<Guid> AffectedIds { get; set; } = new();
}

public sealed record GroupCreateData(Guid Id, string Name, List<Guid> MemberIds);

public sealed record GroupRenameData(Guid Id, string Name);

public sealed record GroupAddData(Guid Id, List<Guid> MemberIds);

public sealed record GroupIdData(Guid Id);

public sealed record ReminderPutData(Guid Id, List<Guid> ParticipantIds, string Json);

public sealed record ReminderData(Guid Id, Guid CreatorId, string Json);

public sealed record ReminderIdData(Guid Id);

public sealed record ReminderDoneData(Guid Id, bool Done, Guid? AccountId = null);

public sealed record SettingsData(string Key, string Value);

public sealed record SignalData(Guid? To, Guid? GroupId, string Type, JsonElement? Payload);
