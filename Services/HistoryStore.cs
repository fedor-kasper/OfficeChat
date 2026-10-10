using System.IO;
using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OfficeChat.Models;

namespace OfficeChat.Services;

/// <summary>Сохранённый собеседник (без признака «в сети» — он известен только во время работы).</summary>
public sealed record StoredContact(Guid Id, string Name, string Machine, IPAddress Address);

/// <summary>
/// Служебный пакет в очереди на отправку конкретному собеседнику (правка, удаление и т. п.).
/// Хранится в базе, чтобы дойти, даже если собеседник появится в сети после перезапуска программы.
/// </summary>
public sealed record OutboxItem(long Seq, Guid PeerId, string PacketJson);

/// <summary>
/// История переписки в локальной базе SQLite (%AppData%\OfficeChat\history.db).
/// Хранится всё: сообщения, их статусы, очередь неотправленных.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private readonly SqliteConnection _db;

    public HistoryStore(string? path = null)
    {
        path ??= Path.Combine(SettingsService.DataFolder, "history.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _db.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS contacts (
                id       TEXT PRIMARY KEY,
                name     TEXT NOT NULL,
                machine  TEXT NOT NULL,
                address  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS messages (
                id                TEXT PRIMARY KEY,
                peer_id           TEXT NOT NULL,
                outgoing          INTEGER NOT NULL,
                text              TEXT NOT NULL,
                timestamp         INTEGER NOT NULL,
                broadcast         INTEGER NOT NULL,
                status            INTEGER NOT NULL,
                is_read           INTEGER NOT NULL,
                read_receipt_sent INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_messages_peer_time ON messages (peer_id, timestamp);
            """);

        // Колонка kind появилась вместе с мини-игрой — добавляем в базы, созданные раньше.
        using (var info = Command("SELECT 1 FROM pragma_table_info('messages') WHERE name = 'kind'"))
        {
            if (info.ExecuteScalar() == null)
                Execute("ALTER TABLE messages ADD COLUMN kind INTEGER NOT NULL DEFAULT 0");
        }
        // Колонки, появившиеся с изображениями и файлами, — добавляем в базы, созданные раньше.
        AddColumnIfMissing("file_name", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("file_path", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("file_size", "INTEGER NOT NULL DEFAULT 0");
        // Ответы и правка сообщений.
        AddColumnIfMissing("reply_to", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("reply_author", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("reply_text", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("edited", "INTEGER NOT NULL DEFAULT 0");
        // Группы: кто автор входящего сообщения.
        AddColumnIfMissing("sender_id", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("sender_name", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("quiet", "INTEGER NOT NULL DEFAULT 0");

        Execute("""
            CREATE TABLE IF NOT EXISTS outbox (
                seq     INTEGER PRIMARY KEY AUTOINCREMENT,
                peer_id TEXT NOT NULL,
                packet  TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS groups (
                id      TEXT PRIMARY KEY,
                name    TEXT NOT NULL,
                members TEXT NOT NULL
            );
            -- Доставка своих сообщений в группе каждому участнику: 0 — ждёт, 1 — доставлено, 2 — прочитано.
            CREATE TABLE IF NOT EXISTS group_receipts (
                message_id TEXT NOT NULL,
                peer_id    TEXT NOT NULL,
                state      INTEGER NOT NULL,
                PRIMARY KEY (message_id, peer_id)
            );
            CREATE INDEX IF NOT EXISTS ix_group_receipts_peer ON group_receipts (peer_id, state);
            -- Группы, из которых мы вышли: их пакеты больше не принимаем.
            CREATE TABLE IF NOT EXISTS left_groups (id TEXT PRIMARY KEY);
            -- Напоминания: каждое целиком в JSON (полей много, искать по ним не нужно).
            CREATE TABLE IF NOT EXISTS reminders (id TEXT PRIMARY KEY, data TEXT NOT NULL);
            """);
    }

    // ---- Контакты ----

    public List<StoredContact> LoadContacts()
    {
        using var cmd = Command("SELECT id, name, machine, address FROM contacts");
        using var reader = cmd.ExecuteReader();
        var result = new List<StoredContact>();
        while (reader.Read())
        {
            result.Add(new StoredContact(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                IPAddress.TryParse(reader.GetString(3), out var address) ? address : IPAddress.None));
        }
        return result;
    }

    public void SaveContact(Peer peer)
    {
        using var cmd = Command("""
            INSERT INTO contacts (id, name, machine, address) VALUES ($id, $name, $machine, $address)
            ON CONFLICT (id) DO UPDATE SET name = $name, machine = $machine, address = $address
            """);
        cmd.Parameters.AddWithValue("$id", peer.Id.ToString());
        cmd.Parameters.AddWithValue("$name", peer.Name);
        cmd.Parameters.AddWithValue("$machine", peer.Machine);
        cmd.Parameters.AddWithValue("$address", peer.Address.ToString());
        cmd.ExecuteNonQuery();
    }

    /// <summary>Удаляет собеседника вместе со всей перепиской.</summary>
    public void DeleteContact(Guid peerId)
    {
        DeleteConversation(peerId);
        using var cmd = Command("DELETE FROM contacts WHERE id = $peer");
        cmd.Parameters.AddWithValue("$peer", peerId.ToString());
        cmd.ExecuteNonQuery();
    }

    // ---- Группы ----

    public List<ChatGroup> LoadGroups()
    {
        using var cmd = Command("SELECT id, name, members FROM groups");
        using var reader = cmd.ExecuteReader();
        var result = new List<ChatGroup>();
        while (reader.Read())
        {
            result.Add(new ChatGroup
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                Members = JsonSerializer.Deserialize<List<GroupMember>>(reader.GetString(2)) ?? new(),
            });
        }
        return result;
    }

    public void SaveGroup(ChatGroup group)
    {
        using var cmd = Command("""
            INSERT INTO groups (id, name, members) VALUES ($id, $name, $members)
            ON CONFLICT (id) DO UPDATE SET name = $name, members = $members
            """);
        cmd.Parameters.AddWithValue("$id", group.Id.ToString());
        cmd.Parameters.AddWithValue("$name", group.Name);
        cmd.Parameters.AddWithValue("$members", JsonSerializer.Serialize(group.Members));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Выход из группы: удаляем её с перепиской и запоминаем, чтобы не принимать её пакеты.</summary>
    public void LeaveGroup(Guid groupId)
    {
        DeleteConversation(groupId);
        using var cmd = Command("""
            DELETE FROM groups WHERE id = $id;
            INSERT OR IGNORE INTO left_groups (id) VALUES ($id);
            """);
        cmd.Parameters.AddWithValue("$id", groupId.ToString());
        cmd.ExecuteNonQuery();
    }

    /// <summary>Нас снова позвали в группу, из которой мы выходили.</summary>
    public void ForgetLeftGroup(Guid groupId)
    {
        using var cmd = Command("DELETE FROM left_groups WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", groupId.ToString());
        cmd.ExecuteNonQuery();
    }

    public bool HasLeftGroup(Guid groupId)
    {
        using var cmd = Command("SELECT 1 FROM left_groups WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", groupId.ToString());
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>Своё сообщение в группе ждёт доставки каждому из этих участников.</summary>
    public void AddReceipts(Guid messageId, IEnumerable<Guid> peerIds)
    {
        using var tx = _db.BeginTransaction();
        using var cmd = Command("INSERT OR IGNORE INTO group_receipts (message_id, peer_id, state) VALUES ($msg, $peer, 0)");
        cmd.Transaction = tx;
        cmd.Parameters.AddWithValue("$msg", messageId.ToString());
        var peer = cmd.Parameters.Add("$peer", SqliteType.Text);
        foreach (var id in peerIds)
        {
            peer.Value = id.ToString();
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    /// <summary>Отметка «доставлено» (1) или «прочитано» (2) от участника. Только повышает состояние.</summary>
    public void SetReceipt(Guid messageId, Guid peerId, int state)
    {
        using var cmd = Command(
            "UPDATE group_receipts SET state = $state WHERE message_id = $msg AND peer_id = $peer AND state < $state");
        cmd.Parameters.AddWithValue("$msg", messageId.ToString());
        cmd.Parameters.AddWithValue("$peer", peerId.ToString());
        cmd.Parameters.AddWithValue("$state", state);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Скольким участникам адресовано, доставлено и прочитано своё сообщение в группе.</summary>
    public (int Total, int Delivered, int Read) ReceiptCounts(Guid messageId)
    {
        using var cmd = Command("""
            SELECT count(*), coalesce(sum(state >= 1), 0), coalesce(sum(state >= 2), 0)
            FROM group_receipts WHERE message_id = $msg
            """);
        cmd.Parameters.AddWithValue("$msg", messageId.ToString());
        using var reader = cmd.ExecuteReader();
        reader.Read();
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    /// <summary>Участники, до которых сообщение уже дошло (им нужно отправлять правку или удаление).</summary>
    public List<Guid> DeliveredTo(Guid messageId)
    {
        using var cmd = Command("SELECT peer_id FROM group_receipts WHERE message_id = $msg AND state >= 1");
        cmd.Parameters.AddWithValue("$msg", messageId.ToString());
        using var reader = cmd.ExecuteReader();
        var result = new List<Guid>();
        while (reader.Read()) result.Add(Guid.Parse(reader.GetString(0)));
        return result;
    }

    /// <summary>Свои сообщения в группах, ещё не доставленные этому участнику, — по порядку.</summary>
    public List<(Guid MessageId, Guid GroupId)> PendingGroupDeliveries(Guid peerId)
    {
        using var cmd = Command("""
            SELECT r.message_id, m.peer_id FROM group_receipts r JOIN messages m ON m.id = r.message_id
            WHERE r.peer_id = $peer AND r.state = 0 ORDER BY m.timestamp
            """);
        cmd.Parameters.AddWithValue("$peer", peerId.ToString());
        using var reader = cmd.ExecuteReader();
        var result = new List<(Guid, Guid)>();
        while (reader.Read()) result.Add((Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1))));
        return result;
    }

    /// <summary>Участники, которым ещё есть что доставить из групп.</summary>
    public List<Guid> PeersWithPendingGroupDeliveries()
    {
        using var cmd = Command("SELECT DISTINCT peer_id FROM group_receipts WHERE state = 0");
        using var reader = cmd.ExecuteReader();
        var result = new List<Guid>();
        while (reader.Read()) result.Add(Guid.Parse(reader.GetString(0)));
        return result;
    }

    // ---- Напоминания ----

    public List<string> LoadReminders()
    {
        using var cmd = Command("SELECT data FROM reminders");
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    public void SaveReminder(Guid id, string json)
    {
        using var cmd = Command("""
            INSERT INTO reminders (id, data) VALUES ($id, $data)
            ON CONFLICT (id) DO UPDATE SET data = $data
            """);
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$data", json);
        cmd.ExecuteNonQuery();
    }

    public void DeleteReminder(Guid id)
    {
        using var cmd = Command("DELETE FROM reminders WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    // ---- Сообщения ----

    /// <summary>Последние <paramref name="limit"/> сообщений раньше <paramref name="before"/>, по возрастанию времени.</summary>
    public List<ChatMessage> LoadRecent(Guid peerId, int limit, DateTime? before = null)
    {
        using var cmd = Command($"""
            SELECT {MessageColumns} FROM messages
            WHERE peer_id = $peer AND timestamp < $before
            ORDER BY timestamp DESC LIMIT $limit
            """);
        cmd.Parameters.AddWithValue("$peer", peerId.ToString());
        cmd.Parameters.AddWithValue("$before", (before ?? DateTime.MaxValue).Ticks);
        cmd.Parameters.AddWithValue("$limit", limit);
        var result = ReadMessages(cmd);
        result.Reverse();
        return result;
    }

    /// <summary>
    /// Сообщения, с которыми ещё есть работа, даже если они старые:
    /// неотправленные, непрочитанные и прочитанные без отправленной отметки.
    /// </summary>
    public List<ChatMessage> LoadPending(Guid peerId)
    {
        using var cmd = Command($"""
            SELECT {MessageColumns} FROM messages
            WHERE peer_id = $peer AND (
                (outgoing = 1 AND status IN ({(int)MessageStatus.Queued}, {(int)MessageStatus.Sending}))
                OR (outgoing = 0 AND (is_read = 0 OR read_receipt_sent = 0)))
            ORDER BY timestamp
            """);
        cmd.Parameters.AddWithValue("$peer", peerId.ToString());
        return ReadMessages(cmd);
    }

    public bool HasMessage(Guid id)
    {
        using var cmd = Command("SELECT 1 FROM messages WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", id.ToString());
        return cmd.ExecuteScalar() != null;
    }

    public void SaveMessage(Guid peerId, ChatMessage message)
    {
        using var cmd = Command("""
            INSERT INTO messages (id, peer_id, outgoing, text, timestamp, broadcast, status, is_read, read_receipt_sent, kind,
                                  file_name, file_path, file_size, reply_to, reply_author, reply_text, edited,
                                  sender_id, sender_name, quiet)
            VALUES ($id, $peer, $outgoing, $text, $timestamp, $broadcast, $status, $isRead, $receipt, $kind, $fileName,
                    $filePath, $fileSize, $replyTo, $replyAuthor, $replyText, $edited, $senderId, $senderName, $quiet)
            ON CONFLICT (id) DO UPDATE SET status = $status, is_read = $isRead, read_receipt_sent = $receipt,
                                           text = $text, edited = $edited
            """);
        cmd.Parameters.AddWithValue("$id", message.Id.ToString());
        cmd.Parameters.AddWithValue("$peer", peerId.ToString());
        cmd.Parameters.AddWithValue("$outgoing", message.IsOutgoing);
        cmd.Parameters.AddWithValue("$text", message.Text);
        cmd.Parameters.AddWithValue("$timestamp", message.Timestamp.Ticks);
        cmd.Parameters.AddWithValue("$broadcast", message.IsBroadcast);
        cmd.Parameters.AddWithValue("$status", (int)message.Status);
        cmd.Parameters.AddWithValue("$isRead", message.IsRead);
        cmd.Parameters.AddWithValue("$receipt", message.ReadReceiptSent);
        cmd.Parameters.AddWithValue("$kind", (int)message.Kind);
        cmd.Parameters.AddWithValue("$fileName", message.FileName);
        cmd.Parameters.AddWithValue("$filePath", message.FilePath);
        cmd.Parameters.AddWithValue("$fileSize", message.FileSize);
        cmd.Parameters.AddWithValue("$replyTo", message.ReplyToId?.ToString() ?? "");
        cmd.Parameters.AddWithValue("$replyAuthor", message.ReplyAuthor);
        cmd.Parameters.AddWithValue("$replyText", message.ReplyText);
        cmd.Parameters.AddWithValue("$edited", message.IsEdited);
        cmd.Parameters.AddWithValue("$senderId", message.SenderId?.ToString() ?? "");
        cmd.Parameters.AddWithValue("$senderName", message.SenderName);
        cmd.Parameters.AddWithValue("$quiet", message.IsQuiet);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Сообщение по Id вместе с перепиской, в которой оно лежит (null — нет такого).</summary>
    public (Guid PeerId, ChatMessage Message)? LoadMessage(Guid id)
    {
        using var cmd = Command($"SELECT {MessageColumns}, peer_id FROM messages WHERE id = $id");
        cmd.Parameters.AddWithValue("$id", id.ToString());
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return (Guid.Parse(reader.GetString(MessageColumnCount)), ReadMessage(reader));
    }

    // ---- Очередь служебных пакетов ----

    public List<OutboxItem> LoadOutbox()
    {
        using var cmd = Command("SELECT seq, peer_id, packet FROM outbox ORDER BY seq");
        using var reader = cmd.ExecuteReader();
        var result = new List<OutboxItem>();
        while (reader.Read())
            result.Add(new OutboxItem(reader.GetInt64(0), Guid.Parse(reader.GetString(1)), reader.GetString(2)));
        return result;
    }

    public OutboxItem AddOutbox(Guid peerId, string packetJson)
    {
        using var cmd = Command("INSERT INTO outbox (peer_id, packet) VALUES ($peer, $packet) RETURNING seq");
        cmd.Parameters.AddWithValue("$peer", peerId.ToString());
        cmd.Parameters.AddWithValue("$packet", packetJson);
        return new OutboxItem((long)cmd.ExecuteScalar()!, peerId, packetJson);
    }

    public void DeleteOutbox(long seq)
    {
        using var cmd = Command("DELETE FROM outbox WHERE seq = $seq");
        cmd.Parameters.AddWithValue("$seq", seq);
        cmd.ExecuteNonQuery();
    }

    public void DeleteOutboxForPeer(Guid peerId)
    {
        using var cmd = Command("DELETE FROM outbox WHERE peer_id = $peer");
        cmd.Parameters.AddWithValue("$peer", peerId.ToString());
        cmd.ExecuteNonQuery();
    }

    /// <summary>Отметка «прочитано» для исходящих, в том числе тех, что сейчас не загружены в окно.</summary>
    public void MarkOutgoingRead(Guid peerId, IEnumerable<Guid> ids)
    {
        using var tx = _db.BeginTransaction();
        using var cmd = Command($"UPDATE messages SET status = {(int)MessageStatus.Read} " +
                                "WHERE id = $id AND peer_id = $peer AND outgoing = 1");
        cmd.Transaction = tx;
        var idParam = cmd.Parameters.Add("$id", SqliteType.Text);
        cmd.Parameters.AddWithValue("$peer", peerId.ToString());
        foreach (var id in ids)
        {
            idParam.Value = id.ToString();
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public void DeleteMessage(Guid id)
    {
        using var cmd = Command("DELETE FROM messages WHERE id = $id; DELETE FROM group_receipts WHERE message_id = $id");
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    /// <summary>Файлы изображений переписки — чтобы удалить их вместе с ней.</summary>
    public List<string> ImagePaths(Guid peerId)
    {
        using var cmd = Command($"SELECT id, file_name FROM messages WHERE peer_id = $peer AND kind = {(int)MessageKind.Image}");
        cmd.Parameters.AddWithValue("$peer", peerId.ToString());
        using var reader = cmd.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
            result.Add(ImageStore.PathFor(Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        return result;
    }

    /// <summary>Удаляет переписку вместе с файлами её изображений и вложений.</summary>
    public void DeleteConversation(Guid peerId)
    {
        var images = ImagePaths(peerId);
        var files = new List<string>();
        using (var list = Command($"SELECT DISTINCT file_path FROM messages WHERE peer_id = $peer AND kind = {(int)MessageKind.File}"))
        {
            list.Parameters.AddWithValue("$peer", peerId.ToString());
            using var reader = list.ExecuteReader();
            while (reader.Read()) files.Add(reader.GetString(0));
        }

        using var cmd = Command("""
            DELETE FROM group_receipts WHERE message_id IN (SELECT id FROM messages WHERE peer_id = $peer);
            DELETE FROM messages WHERE peer_id = $peer;
            """);
        cmd.Parameters.AddWithValue("$peer", peerId.ToString());
        cmd.ExecuteNonQuery();
        foreach (var path in images)
            ImageStore.Delete(path);
        // Файл рассылки «Всем» хранится один на всех — удаляем, только если он больше нигде не нужен.
        foreach (var path in files.Where(p => !IsFileReferenced(p)))
            FileStore.Delete(path);
    }

    /// <summary>Ссылается ли на этот файл хоть одно сообщение.</summary>
    public bool IsFileReferenced(string path)
    {
        using var cmd = Command("SELECT 1 FROM messages WHERE file_path = $path LIMIT 1");
        cmd.Parameters.AddWithValue("$path", path);
        return cmd.ExecuteScalar() != null;
    }

    // ---- Служебное ----

    private const string MessageColumns =
        "id, outgoing, text, timestamp, broadcast, status, is_read, read_receipt_sent, kind, file_name, file_path, file_size, " +
        "reply_to, reply_author, reply_text, edited, sender_id, sender_name, quiet";

    private const int MessageColumnCount = 19;

    private static List<ChatMessage> ReadMessages(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var result = new List<ChatMessage>();
        while (reader.Read())
            result.Add(ReadMessage(reader));
        return result;
    }

    private static ChatMessage ReadMessage(SqliteDataReader reader)
    {
        var status = (MessageStatus)reader.GetInt32(5);
        var id = Guid.Parse(reader.GetString(0));
        var kind = (MessageKind)reader.GetInt32(8);
        var fileName = reader.GetString(9);
        var replyTo = reader.GetString(12);
        var senderId = reader.GetString(16);
        return new ChatMessage
        {
            Id = id,
            IsOutgoing = reader.GetBoolean(1),
            Text = reader.GetString(2),
            Timestamp = new DateTime(reader.GetInt64(3)),
            IsBroadcast = reader.GetBoolean(4),
            // Если программу закрыли посреди отправки — отправим заново.
            Status = status == MessageStatus.Sending ? MessageStatus.Queued : status,
            IsRead = reader.GetBoolean(6),
            ReadReceiptSent = reader.GetBoolean(7),
            Kind = kind,
            FileName = fileName,
            ImagePath = kind == MessageKind.Image ? ImageStore.PathFor(id, fileName) : "",
            FilePath = reader.GetString(10),
            FileSize = reader.GetInt64(11),
            ReplyToId = replyTo.Length > 0 ? Guid.Parse(replyTo) : null,
            ReplyAuthor = reader.GetString(13),
            ReplyText = reader.GetString(14),
            IsEdited = reader.GetBoolean(15),
            SenderId = senderId.Length > 0 ? Guid.Parse(senderId) : null,
            SenderName = reader.GetString(17),
            IsQuiet = reader.GetBoolean(18),
        };
    }

    private SqliteCommand Command(string sql)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    private void AddColumnIfMissing(string column, string definition)
    {
        using var info = Command($"SELECT 1 FROM pragma_table_info('messages') WHERE name = '{column}'");
        if (info.ExecuteScalar() == null)
            Execute($"ALTER TABLE messages ADD COLUMN {column} {definition}");
    }

    private void Execute(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
