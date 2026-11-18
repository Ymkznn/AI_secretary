using AiSecretary.Desktop.Services;
using Microsoft.Data.Sqlite;

namespace AiSecretary.Desktop.Data;

/// <summary>
/// Dedicated SQLite store for security audit logs (guard_audit.db), isolated from user business data (aisecretary.db).
/// </summary>
public sealed class GuardAuditStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public GuardAuditStore()
    {
        _connectionString = $"Data Source={AppPaths.GuardAuditDatabasePath}";
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct = default)
    {
        if (!_initialized)
        {
            await _initLock.WaitAsync(ct);
            try
            {
                if (!_initialized)
                {
                    await using var initConn = new SqliteConnection(_connectionString);
                    await initConn.OpenAsync(ct);
                    await using var cmd = initConn.CreateCommand();
                    cmd.CommandText = """
                        PRAGMA journal_mode=WAL;
                        CREATE TABLE IF NOT EXISTS GuardAudits (
                            Id INTEGER PRIMARY KEY AUTOINCREMENT,
                            SkillName TEXT NOT NULL,
                            Target TEXT NOT NULL,
                            Action TEXT NOT NULL,
                            Reason TEXT NOT NULL,
                            CreatedAt TEXT NOT NULL
                        );
                        """;
                    await cmd.ExecuteNonQueryAsync(ct);
                    _initialized = true;
                }
            }
            finally
            {
                _initLock.Release();
            }
        }

        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    public async Task InsertAuditAsync(string skillName, string target, string action, string reason, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO GuardAudits (SkillName, Target, Action, Reason, CreatedAt)
            VALUES ($skill, $target, $action, $reason, $createdAt);
            """;
        cmd.Parameters.AddWithValue("$skill", skillName);
        cmd.Parameters.AddWithValue("$target", target);
        cmd.Parameters.AddWithValue("$action", action);
        cmd.Parameters.AddWithValue("$reason", reason);
        cmd.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
