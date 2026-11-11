using Microsoft.Data.Sqlite;

namespace Notlar.Core;

/// <summary>Uygulama genelindeki küçük açık/kapalı ayarlar (app_flags tablosu). Aynı SQLite dosyasını
/// (AppPaths.DbPath) diğer store'larla paylaşır; böylece panelde değiştirilen bir ayarı MCP sunucusu her
/// çağrıda güncel olarak okur.</summary>
public sealed class AppFlags
{
    /// <summary>true ise MCP üzerinden kişisel listenin okunmasına (kisisel_listele) izin verilir. Varsayılan: false.</summary>
    public const string KisiselMcpOkuma = "kisisel_mcp_okuma";

    private readonly string _connectionString;
    private readonly IChangeSignal _signal;

    public AppFlags(string dbPath, IChangeSignal? signal = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, DefaultTimeout = 5 }.ToString();
        _signal = signal ?? NullChangeSignal.Instance;

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS app_flags (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public bool GetBool(string key, bool defaultValue = false)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_flags WHERE key = $key";
        cmd.Parameters.AddWithValue("$key", key);
        return cmd.ExecuteScalar() is string value ? value == "1" : defaultValue;
    }

    public void SetBool(string key, bool value)
    {
        using (var conn = Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO app_flags(key, value) VALUES ($key, $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", value ? "1" : "0");
            cmd.ExecuteNonQuery();
        }
        _signal.Notify();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }
}
