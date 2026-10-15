using System.Text.Json;
using Microsoft.Data.Sqlite;
using OfficeChat.Protocol;

namespace OfficeChat.Server.Data;

public sealed record Account(Guid Id, string Login, string DisplayName, string PasswordHash, bool IsAdmin, bool IsBlocked,
    bool MustChangePassword, DateTimeOffset CreatedAt, DateTimeOffset? LastSeen, long DeliveredSeq)
{
    public AccountDto ToDto() => new(Id, Login, DisplayName, IsAdmin, MustChangePassword);
}

public sealed record Device(Guid Id, Guid AccountId, string Name, string Platform, DateTimeOffset CreatedAt,
    DateTimeOffset? LastSeen);

public sealed record StoredEvent(long Seq, string Kind, string Data);

public sealed record StoredMessage(MessageDto Message, bool Deleted);

public sealed record StoredGroup(Guid Id, string Name, List<Guid> Members);

public sealed record StoredReminder(Guid Id, Guid CreatorId, List<Guid> Participants, string Json);

public sealed record StoredFile(Guid Id, Guid OwnerId, string Name, long Size);

/// <summary>
/// База сервера (SQLite). Каждое обращение — своё подключение (пул Microsoft.Data.Sqlite), поэтому методы можно
/// звать из разных потоков. Изменения, которые пишут в журнал событий, идут через <see cref="WriteLock"/> —
/// чтобы номера событий и их порядок совпадали с порядком действий.
/// </summary>
public sealed class ServerDb
{
    private readonly string _connectionString;

    /// <summary>Одна запись за раз: журнал событий должен идти строго по порядку.</summary>
    public SemaphoreSlim WriteLock { get; } = new(1, 1);

    public ServerDb(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
        using var connection = Open();
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS accounts (
                id                   TEXT PRIMARY KEY,
                login                TEXT NOT NULL UNIQUE COLLATE NOCASE,
                display_name         TEXT NOT NULL,
                password_hash        TEXT NOT NULL,
                is_admin             INTEGER NOT NULL DEFAULT 0,
                is_blocked           INTEGER NOT NULL DEFAULT 0,
                must_change_password INTEGER NOT NULL DEFAULT 0,
                created_at           INTEGER NOT NULL,
                last_seen            INTEGER,
                -- до какого события журнала устройства аккаунта подтвердили получение (для «доставлено»)
                delivered_seq        INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS devices (
                id         TEXT PRIMARY KEY,
                account_id TEXT NOT NULL,
                name       TEXT NOT NULL,
                platform   TEXT NOT NULL,
                token_hash TEXT NOT NULL UNIQUE,
                created_at INTEGER NOT NULL,
                last_seen  INTEGER,
                revoked    INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_devices_account ON devices (account_id);
            -- Журнал событий каждого аккаунта: по нему синхронизируются все его устройства.
            CREATE TABLE IF NOT EXISTS events (
                seq        INTEGER PRIMARY KEY AUTOINCREMENT,
                account_id TEXT NOT NULL,
                kind       TEXT NOT NULL,
                data       TEXT NOT NULL,
                message_id TEXT,
                sender_id  TEXT,
                created_at INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_events_account ON events (account_id, seq);
            CREATE TABLE IF NOT EXISTS messages (
                id           TEXT PRIMARY KEY,
                sender_id    TEXT NOT NULL,
                recipient_id TEXT,
                group_id     TEXT,
                file_id      TEXT,
                data         TEXT NOT NULL,
                deleted      INTEGER NOT NULL DEFAULT 0,
                server_time  INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_messages_file ON messages (file_id);
            -- Что с сообщением у получателя: 1 — доставлено, 2 — прочитано.
            CREATE TABLE IF NOT EXISTS message_status (
                message_id TEXT NOT NULL,
                account_id TEXT NOT NULL,
                state      INTEGER NOT NULL,
                PRIMARY KEY (message_id, account_id)
            );
            CREATE TABLE IF NOT EXISTS groups (
                id         TEXT PRIMARY KEY,
                name       TEXT NOT NULL,
                created_by TEXT NOT NULL,
                created_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS group_members (
                group_id   TEXT NOT NULL,
                account_id TEXT NOT NULL,
                PRIMARY KEY (group_id, account_id)
            );
            CREATE TABLE IF NOT EXISTS reminders (
                id           TEXT PRIMARY KEY,
                creator_id   TEXT NOT NULL,
                participants TEXT NOT NULL,
                json         TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS files (
                id         TEXT PRIMARY KEY,
                owner_id   TEXT NOT NULL,
                name       TEXT NOT NULL,
                size       INTEGER NOT NULL,
                created_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (
                account_id TEXT NOT NULL,
                key        TEXT NOT NULL,
                value      TEXT NOT NULL,
                PRIMARY KEY (account_id, key)
            );
            -- Старые Id программы (до аккаунтов, по одному на компьютер): чей это теперь аккаунт.
            CREATE TABLE IF NOT EXISTS legacy_ids (
                legacy_id  TEXT PRIMARY KEY,
                account_id TEXT NOT NULL,
                claimed_at INTEGER NOT NULL
            );
            -- Участники перенесённых групп, ещё не перешедшие на аккаунты: войдут в группу, когда перейдут.
            CREATE TABLE IF NOT EXISTS group_legacy_members (
                group_id  TEXT NOT NULL,
                legacy_id TEXT NOT NULL,
                PRIMARY KEY (group_id, legacy_id)
            );
            """);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 30000;");
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private int NonQuery(string sql, params (string, object?)[] args)
    {
        using var connection = Open();
        using var cmd = Command(connection, sql, args);
        return cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string, object?)[] args)
    {
        using var connection = Open();
        using var cmd = Command(connection, sql, args);
        return cmd.ExecuteScalar();
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        using var connection = Open();
        using var cmd = Command(connection, sql, args);
        using var reader = cmd.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(map(reader));
        return result;
    }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static DateTimeOffset Time(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private static DateTimeOffset? Time(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : Time(reader.GetInt64(column));

    private static string Id(Guid id) => id.ToString();

    // ---- Аккаунты ----

    private const string AccountColumns =
        "id, login, display_name, password_hash, is_admin, is_blocked, must_change_password, created_at, last_seen, delivered_seq";

    private static Account ReadAccount(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5),
        r.GetBoolean(6), Time(r.GetInt64(7)), Time(r, 8), r.GetInt64(9));

    /// <summary>Создаёт аккаунт. false — логин уже занят.</summary>
    public bool CreateAccount(Guid id, string login, string displayName, string passwordHash)
    {
        try
        {
            NonQuery($"INSERT INTO accounts (id, login, display_name, password_hash, created_at) VALUES ($id, $login, $name, $hash, $now)",
                ("$id", Id(id)), ("$login", login), ("$name", displayName), ("$hash", passwordHash), ("$now", Now()));
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19) // UNIQUE
        {
            return false;
        }
    }

    public Account? FindAccount(Guid id) =>
        Query($"SELECT {AccountColumns} FROM accounts WHERE id = $id", ReadAccount, ("$id", Id(id))).FirstOrDefault();

    public Account? FindAccountByLogin(string login) =>
        Query($"SELECT {AccountColumns} FROM accounts WHERE login = $login", ReadAccount, ("$login", login)).FirstOrDefault();

    public List<Account> Accounts() =>
        Query($"SELECT {AccountColumns} FROM accounts ORDER BY display_name COLLATE NOCASE", ReadAccount);

    public bool AccountExists(Guid id) => Scalar("SELECT 1 FROM accounts WHERE id = $id", ("$id", Id(id))) != null;

    public void SetPassword(Guid accountId, string hash, bool mustChange) =>
        NonQuery("UPDATE accounts SET password_hash = $hash, must_change_password = $must WHERE id = $id",
            ("$hash", hash), ("$must", mustChange), ("$id", Id(accountId)));

    public void SetDisplayName(Guid accountId, string name) =>
        NonQuery("UPDATE accounts SET display_name = $name WHERE id = $id", ("$name", name), ("$id", Id(accountId)));

    public void SetAdmin(Guid accountId, bool isAdmin) =>
        NonQuery("UPDATE accounts SET is_admin = $v WHERE id = $id", ("$v", isAdmin), ("$id", Id(accountId)));

    public void SetBlocked(Guid accountId, bool blocked) =>
        NonQuery("UPDATE accounts SET is_blocked = $v WHERE id = $id", ("$v", blocked), ("$id", Id(accountId)));

    public void TouchAccount(Guid accountId) =>
        NonQuery("UPDATE accounts SET last_seen = $now WHERE id = $id", ("$now", Now()), ("$id", Id(accountId)));

    // ---- Устройства ----

    public void AddDevice(Guid id, Guid accountId, string name, string platform, string tokenHash) =>
        NonQuery("""
            INSERT INTO devices (id, account_id, name, platform, token_hash, created_at, last_seen)
            VALUES ($id, $account, $name, $platform, $hash, $now, $now)
            """, ("$id", Id(id)), ("$account", Id(accountId)), ("$name", name), ("$platform", platform),
            ("$hash", tokenHash), ("$now", Now()));

    private static Device ReadDevice(SqliteDataReader r) => new(
        Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetString(2), r.GetString(3), Time(r.GetInt64(4)), Time(r, 5));

    public Device? FindDeviceByToken(string tokenHash) =>
        Query("SELECT id, account_id, name, platform, created_at, last_seen FROM devices WHERE token_hash = $hash AND revoked = 0",
            ReadDevice, ("$hash", tokenHash)).FirstOrDefault();

    public List<Device> Devices(Guid accountId) =>
        Query("SELECT id, account_id, name, platform, created_at, last_seen FROM devices WHERE account_id = $a AND revoked = 0 ORDER BY created_at",
            ReadDevice, ("$a", Id(accountId)));

    public bool RevokeDevice(Guid accountId, Guid deviceId) =>
        NonQuery("UPDATE devices SET revoked = 1 WHERE id = $id AND account_id = $a", ("$id", Id(deviceId)), ("$a", Id(accountId))) > 0;

    /// <summary>Отключить все устройства (сброс пароля, блокировка).</summary>
    public void RevokeAllDevices(Guid accountId) =>
        NonQuery("UPDATE devices SET revoked = 1 WHERE account_id = $a", ("$a", Id(accountId)));

    /// <summary>Устройства, которыми ещё можно пользоваться (не отозваны, аккаунт не заблокирован).</summary>
    public HashSet<Guid> ActiveDevices() =>
        Query("SELECT d.id FROM devices d JOIN accounts a ON a.id = d.account_id WHERE d.revoked = 0 AND a.is_blocked = 0",
            r => Guid.Parse(r.GetString(0))).ToHashSet();

    public void TouchDevice(Guid deviceId) =>
        NonQuery("UPDATE devices SET last_seen = $now WHERE id = $id", ("$now", Now()), ("$id", Id(deviceId)));

    // ---- Журнал событий ----

    /// <summary>
    /// Пишет событие в журналы аккаунтов. Звать под <see cref="WriteLock"/>.
    /// </summary>
    public void AppendEvents(IEnumerable<Guid> accounts, string kind, object data, Guid? messageId = null, Guid? senderId = null)
    {
        var json = JsonSerializer.Serialize(data, ServerProtocol.Json);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var account in accounts.Distinct())
        {
            using var cmd = Command(connection, """
                INSERT INTO events (account_id, kind, data, message_id, sender_id, created_at)
                VALUES ($a, $kind, $data, $msg, $sender, $now)
                """, ("$a", Id(account)), ("$kind", kind), ("$data", json), ("$msg", messageId?.ToString()),
                ("$sender", senderId?.ToString()), ("$now", Now()));
            cmd.Transaction = transaction;
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public List<StoredEvent> EventsAfter(Guid accountId, long since, int limit) =>
        Query("SELECT seq, kind, data FROM events WHERE account_id = $a AND seq > $since ORDER BY seq LIMIT $limit",
            r => new StoredEvent(r.GetInt64(0), r.GetString(1), r.GetString(2)),
            ("$a", Id(accountId)), ("$since", since), ("$limit", limit));

    public long LastSeq(Guid accountId) =>
        Scalar("SELECT MAX(seq) FROM events WHERE account_id = $a", ("$a", Id(accountId))) is long seq ? seq : 0;

    /// <summary>
    /// Устройство подтвердило журнал до <paramref name="seq"/>: какие чужие сообщения теперь доставлены
    /// (по отправителям). Звать под <see cref="WriteLock"/>.
    /// </summary>
    public Dictionary<Guid, List<Guid>> MarkDelivered(Guid accountId, long seq)
    {
        var account = FindAccount(accountId);
        var result = new Dictionary<Guid, List<Guid>>();
        if (account == null || seq <= account.DeliveredSeq) return result;
        var delivered = Query("""
            SELECT message_id, sender_id FROM events
            WHERE account_id = $a AND seq > $from AND seq <= $to AND kind = $kind AND sender_id IS NOT NULL AND sender_id <> $a
            """, r => (Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1))),
            ("$a", Id(accountId)), ("$from", account.DeliveredSeq), ("$to", seq), ("$kind", EventKinds.Message));
        NonQuery("UPDATE accounts SET delivered_seq = $seq WHERE id = $a AND delivered_seq < $seq", ("$seq", seq), ("$a", Id(accountId)));
        foreach (var (messageId, senderId) in delivered)
        {
            var inserted = NonQuery("INSERT OR IGNORE INTO message_status (message_id, account_id, state) VALUES ($m, $a, 1)",
                ("$m", Id(messageId)), ("$a", Id(accountId)));
            if (inserted == 0) continue;
            if (!result.TryGetValue(senderId, out var list)) result[senderId] = list = new List<Guid>();
            list.Add(messageId);
        }
        return result;
    }

    /// <summary>Отмечает прочитанными; возвращает новые отметки по отправителям.</summary>
    public Dictionary<Guid, List<Guid>> MarkRead(Guid accountId, IEnumerable<Guid> messageIds)
    {
        var result = new Dictionary<Guid, List<Guid>>();
        foreach (var id in messageIds.Distinct())
        {
            var message = FindMessage(id);
            if (message == null || message.Deleted || message.Message.SenderId == accountId) continue;
            if (!CanSee(accountId, message.Message)) continue;
            var changed = NonQuery("""
                INSERT INTO message_status (message_id, account_id, state) VALUES ($m, $a, 2)
                ON CONFLICT (message_id, account_id) DO UPDATE SET state = 2 WHERE state < 2
                """, ("$m", Id(id)), ("$a", Id(accountId)));
            if (changed == 0) continue;
            var sender = message.Message.SenderId;
            if (!result.TryGetValue(sender, out var list)) result[sender] = list = new List<Guid>();
            list.Add(id);
        }
        return result;
    }

    // ---- Сообщения ----

    /// <summary>Сохраняет сообщение. false — сообщение с таким Id уже есть.</summary>
    public bool AddMessage(MessageDto message)
    {
        try
        {
            NonQuery("""
                INSERT INTO messages (id, sender_id, recipient_id, group_id, file_id, data, server_time)
                VALUES ($id, $sender, $recipient, $group, $file, $data, $time)
                """, ("$id", Id(message.Id)), ("$sender", Id(message.SenderId)), ("$recipient", message.RecipientId?.ToString()),
                ("$group", message.GroupId?.ToString()), ("$file", message.FileId?.ToString()),
                ("$data", JsonSerializer.Serialize(message, ServerProtocol.Json)), ("$time", message.ServerTime.ToUnixTimeMilliseconds()));
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    public StoredMessage? FindMessage(Guid id) =>
        Query("SELECT data, deleted FROM messages WHERE id = $id",
            r => new StoredMessage(JsonSerializer.Deserialize<MessageDto>(r.GetString(0), ServerProtocol.Json)!, r.GetBoolean(1)),
            ("$id", Id(id))).FirstOrDefault();

    public void UpdateMessage(MessageDto message) =>
        NonQuery("UPDATE messages SET data = $data WHERE id = $id",
            ("$data", JsonSerializer.Serialize(message, ServerProtocol.Json)), ("$id", Id(message.Id)));

    public void MarkMessageDeleted(Guid id) =>
        NonQuery("UPDATE messages SET deleted = 1 WHERE id = $id", ("$id", Id(id)));

    /// <summary>Кому видно сообщение: участникам личной переписки или членам группы.</summary>
    public List<Guid> AudienceOf(MessageDto message) =>
        message.GroupId is { } groupId
            ? GroupMembers(groupId)
            : new List<Guid> { message.SenderId, message.RecipientId ?? message.SenderId }.Distinct().ToList();

    public bool CanSee(Guid accountId, MessageDto message) =>
        message.SenderId == accountId || message.RecipientId == accountId ||
        (message.GroupId is { } groupId && IsGroupMember(groupId, accountId));

    // ---- Старые Id (до аккаунтов) ----

    public List<LegacyMapping> LegacyMappings() =>
        Query("SELECT legacy_id, account_id FROM legacy_ids",
            r => new LegacyMapping(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1))));

    /// <summary>Аккаунт по Id: сам аккаунт или старый Id, перенесённый в аккаунт. null — неизвестно.</summary>
    public Guid? ResolveAccount(Guid id)
    {
        if (AccountExists(id)) return id;
        return Scalar("SELECT account_id FROM legacy_ids WHERE legacy_id = $id", ("$id", Id(id))) is string account
            ? Guid.Parse(account)
            : null;
    }

    /// <summary>
    /// Закрепить старый Id за аккаунтом. true — теперь (или уже был) за ним; false — за другим аккаунтом.
    /// Сообщения, где он был отправителем или получателем, переписываются на аккаунт.
    /// </summary>
    public bool ClaimLegacy(Guid accountId, Guid legacyId)
    {
        var owner = Scalar("SELECT account_id FROM legacy_ids WHERE legacy_id = $id", ("$id", Id(legacyId))) as string;
        if (owner != null) return owner == Id(accountId);
        if (AccountExists(legacyId)) return false;
        NonQuery("INSERT INTO legacy_ids (legacy_id, account_id, claimed_at) VALUES ($l, $a, $now)",
            ("$l", Id(legacyId)), ("$a", Id(accountId)), ("$now", Now()));
        // В данных сообщения Id лежат той же строкой — заменяем и там.
        NonQuery("UPDATE messages SET sender_id = $a, data = replace(data, $l, $a) WHERE sender_id = $l",
            ("$l", Id(legacyId)), ("$a", Id(accountId)));
        NonQuery("UPDATE messages SET recipient_id = $a, data = replace(data, $l, $a) WHERE recipient_id = $l",
            ("$l", Id(legacyId)), ("$a", Id(accountId)));
        return true;
    }

    /// <summary>Группы, куда этот старый Id ждали; ожидание снимается (аккаунт добавляет вызывающий).</summary>
    public List<Guid> TakePendingGroups(Guid legacyId)
    {
        var groups = Query("SELECT group_id FROM group_legacy_members WHERE legacy_id = $l",
            r => Guid.Parse(r.GetString(0)), ("$l", Id(legacyId)));
        NonQuery("DELETE FROM group_legacy_members WHERE legacy_id = $l", ("$l", Id(legacyId)));
        return groups;
    }

    public void AddPendingGroupMember(Guid groupId, Guid legacyId) =>
        NonQuery("INSERT OR IGNORE INTO group_legacy_members (group_id, legacy_id) VALUES ($g, $l)",
            ("$g", Id(groupId)), ("$l", Id(legacyId)));

    public bool IsPendingGroupMember(Guid groupId, Guid legacyId) =>
        Scalar("SELECT 1 FROM group_legacy_members WHERE group_id = $g AND legacy_id = $l",
            ("$g", Id(groupId)), ("$l", Id(legacyId))) != null;

    // ---- Группы ----

    public bool CreateGroup(Guid id, string name, Guid createdBy, IEnumerable<Guid> members)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        try
        {
            using (var cmd = Command(connection, "INSERT INTO groups (id, name, created_by, created_at) VALUES ($id, $name, $by, $now)",
                       ("$id", Id(id)), ("$name", name), ("$by", Id(createdBy)), ("$now", Now())))
            {
                cmd.Transaction = transaction;
                cmd.ExecuteNonQuery();
            }
            foreach (var member in members.Distinct())
            {
                using var cmd = Command(connection, "INSERT INTO group_members (group_id, account_id) VALUES ($g, $a)",
                    ("$g", Id(id)), ("$a", Id(member)));
                cmd.Transaction = transaction;
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            return false;
        }
    }

    public StoredGroup? FindGroup(Guid id)
    {
        var name = Scalar("SELECT name FROM groups WHERE id = $id", ("$id", Id(id))) as string;
        return name == null ? null : new StoredGroup(id, name, GroupMembers(id));
    }

    public List<Guid> GroupMembers(Guid groupId) =>
        Query("SELECT account_id FROM group_members WHERE group_id = $g", r => Guid.Parse(r.GetString(0)), ("$g", Id(groupId)));

    public bool IsGroupMember(Guid groupId, Guid accountId) =>
        Scalar("SELECT 1 FROM group_members WHERE group_id = $g AND account_id = $a", ("$g", Id(groupId)), ("$a", Id(accountId))) != null;

    public void RenameGroup(Guid groupId, string name) =>
        NonQuery("UPDATE groups SET name = $name WHERE id = $id", ("$name", name), ("$id", Id(groupId)));

    public void AddGroupMember(Guid groupId, Guid accountId) =>
        NonQuery("INSERT OR IGNORE INTO group_members (group_id, account_id) VALUES ($g, $a)", ("$g", Id(groupId)), ("$a", Id(accountId)));

    public void RemoveGroupMember(Guid groupId, Guid accountId) =>
        NonQuery("DELETE FROM group_members WHERE group_id = $g AND account_id = $a", ("$g", Id(groupId)), ("$a", Id(accountId)));

    // ---- Напоминания ----

    public StoredReminder? FindReminder(Guid id) =>
        Query("SELECT creator_id, participants, json FROM reminders WHERE id = $id",
            r => new StoredReminder(id, Guid.Parse(r.GetString(0)),
                JsonSerializer.Deserialize<List<Guid>>(r.GetString(1)) ?? new List<Guid>(), r.GetString(2)),
            ("$id", Id(id))).FirstOrDefault();

    public void SaveReminder(StoredReminder reminder) =>
        NonQuery("""
            INSERT INTO reminders (id, creator_id, participants, json) VALUES ($id, $creator, $participants, $json)
            ON CONFLICT (id) DO UPDATE SET participants = $participants, json = $json
            """, ("$id", Id(reminder.Id)), ("$creator", Id(reminder.CreatorId)),
            ("$participants", JsonSerializer.Serialize(reminder.Participants)), ("$json", reminder.Json));

    public void DeleteReminder(Guid id) => NonQuery("DELETE FROM reminders WHERE id = $id", ("$id", Id(id)));

    // ---- Файлы ----

    public void AddFile(StoredFile file) =>
        NonQuery("INSERT INTO files (id, owner_id, name, size, created_at) VALUES ($id, $owner, $name, $size, $now)",
            ("$id", Id(file.Id)), ("$owner", Id(file.OwnerId)), ("$name", file.Name), ("$size", file.Size), ("$now", Now()));

    public StoredFile? FindFile(Guid id) =>
        Query("SELECT owner_id, name, size FROM files WHERE id = $id",
            r => new StoredFile(id, Guid.Parse(r.GetString(0)), r.GetString(1), r.GetInt64(2)), ("$id", Id(id))).FirstOrDefault();

    /// <summary>Файл можно скачать тому, кто его загрузил, и участникам переписки, где он отправлен.</summary>
    public bool CanReadFile(Guid accountId, StoredFile file) =>
        file.OwnerId == accountId ||
        Scalar("""
            SELECT 1 FROM messages m
            WHERE m.file_id = $f AND m.deleted = 0 AND (m.sender_id = $a OR m.recipient_id = $a OR
                  m.group_id IN (SELECT group_id FROM group_members WHERE account_id = $a))
            LIMIT 1
            """, ("$f", Id(file.Id)), ("$a", Id(accountId))) != null;

    public long TotalFileSize() => Scalar("SELECT COALESCE(SUM(size), 0) FROM files") is long size ? size : 0;

    // ---- Настройки аккаунта ----

    public void SaveSetting(Guid accountId, string key, string value) =>
        NonQuery("""
            INSERT INTO settings (account_id, key, value) VALUES ($a, $k, $v)
            ON CONFLICT (account_id, key) DO UPDATE SET value = $v
            """, ("$a", Id(accountId)), ("$k", key), ("$v", value));
}
