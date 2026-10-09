using System.IO;
using System.Net;
using Microsoft.Data.Sqlite;
using OfficeChat.Models;

namespace OfficeChat.Services;

/// <summary>Сохранённый собеседник (без признака «в сети» — он известен только во время работы).</summary>
public sealed record StoredContact(Guid Id, string Name, string Machine, IPAddress Address);

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
        // Колонка file_name — с изображениями.
        using (var info = Command("SELECT 1 FROM pragma_table_info('messages') WHERE name = 'file_name'"))
        {
            if (info.ExecuteScalar() == null)
                Execute("ALTER TABLE messages ADD COLUMN file_name TEXT NOT NULL DEFAULT ''");
        }
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
            INSERT INTO messages (id, peer_id, outgoing, text, timestamp, broadcast, status, is_read, read_receipt_sent, kind, file_name)
            VALUES ($id, $peer, $outgoing, $text, $timestamp, $broadcast, $status, $isRead, $receipt, $kind, $fileName)
            ON CONFLICT (id) DO UPDATE SET status = $status, is_read = $isRead, read_receipt_sent = $receipt
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
        using var cmd = Command("DELETE FROM messages WHERE id = $id");
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

    /// <summary>Удаляет переписку вместе с файлами её изображений.</summary>
    public void DeleteConversation(Guid peerId)
    {
        var images = ImagePaths(peerId);
        using var cmd = Command("DELETE FROM messages WHERE peer_id = $peer");
        cmd.Parameters.AddWithValue("$peer", peerId.ToString());
        cmd.ExecuteNonQuery();
        foreach (var path in images)
            ImageStore.Delete(path);
    }

    // ---- Служебное ----

    private const string MessageColumns =
        "id, outgoing, text, timestamp, broadcast, status, is_read, read_receipt_sent, kind, file_name";

    private static List<ChatMessage> ReadMessages(SqliteCommand cmd)
    {
        using var reader = cmd.ExecuteReader();
        var result = new List<ChatMessage>();
        while (reader.Read())
        {
            var status = (MessageStatus)reader.GetInt32(5);
            var id = Guid.Parse(reader.GetString(0));
            var kind = (MessageKind)reader.GetInt32(8);
            var fileName = reader.GetString(9);
            result.Add(new ChatMessage
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
            });
        }
        return result;
    }

    private SqliteCommand Command(string sql)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    private void Execute(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
