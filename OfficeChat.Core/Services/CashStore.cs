using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OfficeChat.Models;

namespace OfficeChat.Services;

/// <summary>Журнал кассы на диске (cash.db в папке данных): только добавление записей.</summary>
public sealed class CashStore : IDisposable
{
    private readonly SqliteConnection _db;

    public CashStore(string? path = null)
    {
        path ??= Path.Combine(SettingsService.DataFolder, "cash.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _db.Open();
        using var create = _db.CreateCommand();
        create.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS operations (
                id      TEXT PRIMARY KEY,
                device  TEXT NOT NULL,
                seq     INTEGER NOT NULL,
                json    TEXT NOT NULL,
                UNIQUE (device, seq)
            );
            """;
        create.ExecuteNonQuery();
    }

    public List<CashOperation> LoadAll()
    {
        var result = new List<CashOperation>();
        using var command = _db.CreateCommand();
        command.CommandText = "SELECT json FROM operations";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            try
            {
                if (JsonSerializer.Deserialize<CashOperation>(reader.GetString(0)) is { } op) result.Add(op);
            }
            catch (JsonException ex)
            {
                Log.Warn("Повреждённая запись кассы в базе — пропускаем", ex);
            }
        }
        return result;
    }

    /// <summary>Добавить запись. false — такая уже есть (тот же Id или тот же номер с того же компьютера).</summary>
    public bool Add(CashOperation op)
    {
        using var command = _db.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO operations (id, device, seq, json) VALUES ($id, $device, $seq, $json)";
        command.Parameters.AddWithValue("$id", op.Id.ToString());
        command.Parameters.AddWithValue("$device", op.DeviceId.ToString());
        command.Parameters.AddWithValue("$seq", op.Seq);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(op));
        return command.ExecuteNonQuery() > 0;
    }

    public void Dispose() => _db.Dispose();
}
