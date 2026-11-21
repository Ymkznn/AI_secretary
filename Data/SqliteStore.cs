using System.Text.Json;
using AiSecretary.Desktop.Models;
using AiSecretary.Desktop.Services;
using Microsoft.Data.Sqlite;

namespace AiSecretary.Desktop.Data;

public sealed class SqliteStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqliteStore(IConfiguration config)
    {
        var configured = config.GetValue<string>("Database:ConnectionString");
        _connectionString = string.IsNullOrWhiteSpace(configured) ||
                            configured.Trim().Equals("Data Source=aisecretary.db", StringComparison.OrdinalIgnoreCase)
            ? $"Data Source={AppPaths.DatabasePath}"
            : configured;
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
                    await CreateSchemaAsync(initConn, ct);
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

    private static async Task CreateSchemaAsync(SqliteConnection conn, CancellationToken ct)
    {
        var sql = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS ChatMessages (
                Id TEXT PRIMARY KEY,
                ConversationId TEXT NOT NULL,
                Role TEXT NOT NULL,
                Content TEXT NOT NULL,
                StatusText TEXT NULL,
                IsStreaming INTEGER NOT NULL DEFAULT 0,
                TaskId TEXT NULL,
                SourcesJson TEXT NOT NULL DEFAULT '[]',
                NotificationJson TEXT NULL,
                DraftJson TEXT NULL,
                CreatedAt TEXT NOT NULL,
                ReplyToMessageId TEXT NULL,
                RevealedAt TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ChatMessages_Conv ON ChatMessages(ConversationId, CreatedAt);

            CREATE TABLE IF NOT EXISTS MailEvents (
                EventId TEXT PRIMARY KEY,
                MailId TEXT NOT NULL UNIQUE,
                RuleDecision TEXT NOT NULL,
                Processed INTEGER NOT NULL DEFAULT 0,
                DetectedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Notifications (
                NotificationId TEXT PRIMARY KEY,
                EventId TEXT NOT NULL,
                MailId TEXT NOT NULL UNIQUE,
                ConversationId TEXT NOT NULL,
                Title TEXT NOT NULL,
                Summary TEXT NOT NULL,
                Status TEXT NOT NULL,
                SnoozeUntil TEXT NULL,
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AgentTasks (
                TaskId TEXT PRIMARY KEY,
                ConversationId TEXT NOT NULL,
                Goal TEXT NOT NULL,
                Status TEXT NOT NULL,
                TriggerEventId TEXT NULL,
                ResultSummary TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS AgentSessions (
                SessionId TEXT PRIMARY KEY,
                ConversationId TEXT NOT NULL,
                RoleName TEXT NOT NULL,
                EstimatedTokens INTEGER NOT NULL DEFAULT 0,
                IsActive INTEGER NOT NULL DEFAULT 1,
                HandoffJson TEXT NOT NULL DEFAULT '{}',
                CreatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS UserMemories (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS SyncState (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS Reminders (
                ReminderId TEXT PRIMARY KEY,
                ConversationId TEXT NOT NULL,
                Title TEXT NOT NULL,
                Message TEXT NOT NULL,
                RelatedMailId TEXT NULL,
                RemindAt TEXT NOT NULL,
                Status TEXT NOT NULL,
                IntervalMinutes INTEGER NOT NULL DEFAULT 0,
                SkillName TEXT NULL,
                SkillArgsJson TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Skills (
                Name TEXT PRIMARY KEY,
                Kind TEXT NOT NULL DEFAULT 'basic',
                HandlerType TEXT NULL,
                DisplayDescription TEXT NOT NULL,
                MainAgentSpec TEXT NOT NULL DEFAULT '',
                PlannerSpec TEXT NOT NULL DEFAULT '',
                Command TEXT NULL,
                AliasesJson TEXT NOT NULL DEFAULT '[]',
                Capability TEXT NULL,
                IsSilentPoller INTEGER NOT NULL DEFAULT 0,
                IsEnabled INTEGER NOT NULL DEFAULT 1,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Skills_Kind ON Skills(Kind, Name);

            CREATE TABLE IF NOT EXISTS GuardAuditLogs (
                Id TEXT PRIMARY KEY,
                Timestamp TEXT NOT NULL,
                SkillName TEXT NOT NULL,
                Target TEXT NOT NULL,
                Action TEXT NOT NULL,
                Reason TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS KnowledgeFacts (
                FactId TEXT PRIMARY KEY,
                ConversationId TEXT NOT NULL,
                MemoryKey TEXT NOT NULL DEFAULT '',
                Category TEXT NOT NULL DEFAULT 'general',
                Summary TEXT NOT NULL,
                Detail TEXT NOT NULL,
                Evidence TEXT NULL,
                Confidence TEXT NOT NULL DEFAULT 'observed',
                State TEXT NOT NULL DEFAULT 'Confirmed',
                ReinforceCount INTEGER NOT NULL DEFAULT 1,
                ExpiresAt TEXT NULL,
                LastVerifiedAt TEXT NULL,
                TagsJson TEXT NOT NULL DEFAULT '[]',
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_KnowledgeFacts_Conv ON KnowledgeFacts(ConversationId, CreatedAt);
            CREATE INDEX IF NOT EXISTS IX_KnowledgeFacts_Category ON KnowledgeFacts(Category);
            CREATE TABLE IF NOT EXISTS CalendarEvents (
                EventId TEXT PRIMARY KEY,
                ConversationId TEXT NOT NULL,
                Title TEXT NOT NULL,
                Category TEXT NOT NULL DEFAULT 'event',
                StartAt TEXT NOT NULL,
                EndAt TEXT NOT NULL,
                AllDay INTEGER NOT NULL DEFAULT 0,
                Notes TEXT NOT NULL DEFAULT '',
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_CalendarEvents_Conv ON CalendarEvents(ConversationId, StartAt);
            CREATE TABLE IF NOT EXISTS StandingWatchers (
                WatcherId TEXT PRIMARY KEY,
                ConversationId TEXT NOT NULL,
                Title TEXT NOT NULL,
                Kind TEXT NOT NULL,
                Url TEXT NOT NULL,
                SelectorOrPath TEXT NOT NULL,
                ConditionType TEXT NOT NULL,
                ThresholdValue TEXT NULL,
                IntervalSeconds INTEGER NOT NULL,
                LastSnapshot TEXT NULL,
                LastCheckedAt TEXT NULL,
                NextCheckAt TEXT NULL,
                FailureCount INTEGER NOT NULL DEFAULT 0,
                Status TEXT NOT NULL DEFAULT 'Active',
                CreatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_StandingWatchers_Status ON StandingWatchers(Status, NextCheckAt);
            CREATE TABLE IF NOT EXISTS WorkflowCaches (
                WorkflowId TEXT PRIMARY KEY,
                ConversationId TEXT NOT NULL,
                GoalSignature TEXT NOT NULL,
                StepsJson TEXT NOT NULL,
                PredictorCounter INTEGER NOT NULL DEFAULT 1,
                HitCount INTEGER NOT NULL DEFAULT 0,
                MissCount INTEGER NOT NULL DEFAULT 0,
                Status TEXT NOT NULL DEFAULT 'Cold',
                LastUsedAt TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_WorkflowCaches_Conv ON WorkflowCaches(ConversationId, PredictorCounter);
            CREATE TABLE IF NOT EXISTS BranchTelemetry (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Predictions INTEGER NOT NULL DEFAULT 0,
                Hits INTEGER NOT NULL DEFAULT 0,
                Mispredictions INTEGER NOT NULL DEFAULT 0,
                EstimatedTokensSaved INTEGER NOT NULL DEFAULT 0
            );
            INSERT OR IGNORE INTO BranchTelemetry (Id, Predictions, Hits, Mispredictions, EstimatedTokensSaved)
            VALUES (1, 0, 0, 0, 0);
            CREATE TABLE IF NOT EXISTS UiLocaleCache (
                Language TEXT NOT NULL,
                MsgKey TEXT NOT NULL,
                Template TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (Language, MsgKey)
            );
            CREATE TABLE IF NOT EXISTS AutoStateRules (
                TriggerKey TEXT PRIMARY KEY,
                StateName TEXT NOT NULL,
                StepsJson TEXT NOT NULL DEFAULT '[]',
                PredictorCounter INTEGER NOT NULL DEFAULT 1,
                HitCount INTEGER NOT NULL DEFAULT 0,
                MissCount INTEGER NOT NULL DEFAULT 0,
                Status TEXT NOT NULL DEFAULT 'Cold',
                LastSeenAt TEXT NOT NULL,
                CooldownUntil TEXT NULL,
                ProposalText TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS MemoryAnnotations (
                AnnotationId TEXT PRIMARY KEY,
                ConversationId TEXT NOT NULL,
                TargetType TEXT NOT NULL,
                TargetId TEXT NOT NULL,
                Note TEXT NOT NULL,
                SourceFactId TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_MemoryAnnotations_Target ON MemoryAnnotations(TargetType, TargetId);
            CREATE INDEX IF NOT EXISTS IX_MemoryAnnotations_SourceFact ON MemoryAnnotations(SourceFactId);
            CREATE TABLE IF NOT EXISTS LlmCallLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TimestampUtc TEXT NOT NULL,
                Agent TEXT NOT NULL,
                Tier TEXT NOT NULL,
                Model TEXT NOT NULL,
                CachePolicy TEXT NOT NULL,
                Priority TEXT NOT NULL,
                UncachedInput INTEGER NOT NULL DEFAULT 0,
                CacheWrite INTEGER NOT NULL DEFAULT 0,
                CacheRead INTEGER NOT NULL DEFAULT 0,
                Output INTEGER NOT NULL DEFAULT 0,
                LatencyMs INTEGER NOT NULL DEFAULT 0,
                Succeeded INTEGER NOT NULL DEFAULT 1,
                Error TEXT NULL,
                StopReason TEXT NULL,
                RawTextLength INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS IX_LlmCallLogs_Ts ON LlmCallLogs(TimestampUtc DESC);
            CREATE TABLE IF NOT EXISTS LearningEvents (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TimestampUtc TEXT NOT NULL,
                Learner TEXT NOT NULL,
                SubjectKey TEXT NOT NULL,
                EventType TEXT NOT NULL,
                BeforeState TEXT NULL,
                AfterState TEXT NULL,
                Evidence TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_LearningEvents_Ts ON LearningEvents(TimestampUtc DESC);
            CREATE TABLE IF NOT EXISTS PredictionLogs (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TimestampUtc TEXT NOT NULL,
                Source TEXT NOT NULL,
                SubjectKey TEXT NOT NULL,
                QueryText TEXT NOT NULL,
                PredictedAction TEXT NOT NULL,
                ExecSucceeded INTEGER NOT NULL DEFAULT 0,
                ImplicitFeedback TEXT NULL,
                TokensSaved INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS IX_PredictionLogs_Ts ON PredictionLogs(TimestampUtc DESC);
            CREATE TABLE IF NOT EXISTS AutonomyPolicies (
                ActionClass TEXT NOT NULL,
                Domain TEXT NOT NULL,
                Level INTEGER NOT NULL DEFAULT 1,
                Accepted INTEGER NOT NULL DEFAULT 0,
                Rejected INTEGER NOT NULL DEFAULT 0,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (ActionClass, Domain)
            );
            """;

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);

        // Safe column migration for existing databases
        foreach (var alter in new[]
        {
            "ALTER TABLE Reminders ADD COLUMN IntervalMinutes INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE Reminders ADD COLUMN SkillName TEXT NULL;",
            "ALTER TABLE Reminders ADD COLUMN SkillArgsJson TEXT NULL;",
            "ALTER TABLE Reminders ADD COLUMN WorkdaysOnly INTEGER NOT NULL DEFAULT 0;",
            "ALTER TABLE Reminders ADD COLUMN ExceptionDatesJson TEXT NOT NULL DEFAULT '[]';",
            "ALTER TABLE Reminders ADD COLUMN LinkedEventId TEXT NULL;",
            "ALTER TABLE Reminders ADD COLUMN RecurrenceRule TEXT NULL;",
            "ALTER TABLE CalendarEvents ADD COLUMN Recurrence TEXT NOT NULL DEFAULT 'none';",
            "ALTER TABLE CalendarEvents ADD COLUMN ExceptionDatesJson TEXT NOT NULL DEFAULT '[]';",
            "ALTER TABLE KnowledgeFacts ADD COLUMN MemoryKey TEXT NOT NULL DEFAULT '';",
            "ALTER TABLE KnowledgeFacts ADD COLUMN State TEXT NOT NULL DEFAULT 'Confirmed';",
            "ALTER TABLE KnowledgeFacts ADD COLUMN ReinforceCount INTEGER NOT NULL DEFAULT 1;",
            "ALTER TABLE KnowledgeFacts ADD COLUMN ExpiresAt TEXT NULL;",
            "ALTER TABLE KnowledgeFacts ADD COLUMN LastVerifiedAt TEXT NULL;",
            "ALTER TABLE AutoStateRules ADD COLUMN CooldownUntil TEXT NULL;",
            "ALTER TABLE AutoStateRules ADD COLUMN ProposalText TEXT NULL;",
            "ALTER TABLE ChatMessages ADD COLUMN ReplyToMessageId TEXT NULL;",
            "ALTER TABLE ChatMessages ADD COLUMN RevealedAt TEXT NULL;",
            "ALTER TABLE LlmCallLogs ADD COLUMN StopReason TEXT NULL;",
            "ALTER TABLE LlmCallLogs ADD COLUMN RawTextLength INTEGER NOT NULL DEFAULT 0;"
        })
        {
            try
            {
                await using var ac = conn.CreateCommand();
                ac.CommandText = alter;
                await ac.ExecuteNonQueryAsync(ct);
            }
            catch
            {
                // Column already exists
            }
        }

        await using (var idxCmd = conn.CreateCommand())
        {
            idxCmd.CommandText = "CREATE INDEX IF NOT EXISTS IX_ChatMessages_Conv_Revealed ON ChatMessages(ConversationId, RevealedAt, CreatedAt);";
            await idxCmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<string> GetActiveConversationIdAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM SyncState WHERE Key = 'active_conversation_id';";
        var res = await cmd.ExecuteScalarAsync(ct) as string;
        if (!string.IsNullOrWhiteSpace(res)) return res;

        var defaultId = "conv-main";
        await SetActiveConversationIdAsync(defaultId, ct);
        return defaultId;
    }

    public async Task SetActiveConversationIdAsync(string conversationId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SyncState (Key, Value) VALUES ('active_conversation_id', $val)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        cmd.Parameters.AddWithValue("$val", conversationId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DateTimeOffset?> GetMailSyncCursorAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM SyncState WHERE Key = 'mail_sync_cursor';";
        var res = await cmd.ExecuteScalarAsync(ct) as string;
        return DateTimeOffset.TryParse(res, out var dto) ? dto : null;
    }

    public async Task SetMailSyncCursorAsync(DateTimeOffset cursor, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SyncState (Key, Value) VALUES ('mail_sync_cursor', $val)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        cmd.Parameters.AddWithValue("$val", cursor.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertChatMessageAsync(ChatMessageRecord msg, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO ChatMessages (Id, ConversationId, Role, Content, StatusText, IsStreaming, TaskId, SourcesJson, NotificationJson, DraftJson, CreatedAt, ReplyToMessageId, RevealedAt)
            VALUES ($id, $conv, $role, $content, $status, $streaming, $taskId, $sources, $notif, $draft, $created, $replyTo, $revealed)
            ON CONFLICT(Id) DO UPDATE SET
                Content = excluded.Content,
                StatusText = excluded.StatusText,
                IsStreaming = excluded.IsStreaming,
                SourcesJson = excluded.SourcesJson,
                NotificationJson = excluded.NotificationJson,
                DraftJson = excluded.DraftJson,
                ReplyToMessageId = COALESCE(excluded.ReplyToMessageId, ChatMessages.ReplyToMessageId),
                RevealedAt = COALESCE(excluded.RevealedAt, ChatMessages.RevealedAt);
            """;
        cmd.Parameters.AddWithValue("$id", msg.Id);
        cmd.Parameters.AddWithValue("$conv", msg.ConversationId);
        cmd.Parameters.AddWithValue("$role", msg.Role);
        cmd.Parameters.AddWithValue("$content", msg.Content);
        cmd.Parameters.AddWithValue("$status", (object?)msg.StatusText ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$streaming", msg.IsStreaming ? 1 : 0);
        cmd.Parameters.AddWithValue("$taskId", (object?)msg.TaskId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(msg.Sources));
        cmd.Parameters.AddWithValue("$notif", msg.Notification is null ? DBNull.Value : JsonSerializer.Serialize(msg.Notification));
        cmd.Parameters.AddWithValue("$draft", DBNull.Value);
        cmd.Parameters.AddWithValue("$created", msg.CreatedAt.ToUniversalTime().ToString("O"));
        cmd.Parameters.AddWithValue("$replyTo", (object?)msg.ReplyToMessageId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$revealed", msg.RevealedAt.HasValue ? msg.RevealedAt.Value.ToUniversalTime().ToString("O") : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<ChatMessageRecord>> GetChatMessagesAsync(string conversationId, int limit = 100, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, ConversationId, Role, Content, StatusText, IsStreaming, TaskId, SourcesJson, NotificationJson, DraftJson, CreatedAt, ReplyToMessageId, RevealedAt
            FROM (
                SELECT Id, ConversationId, Role, Content, StatusText, IsStreaming, TaskId, SourcesJson, NotificationJson, DraftJson, CreatedAt, ReplyToMessageId, RevealedAt
                FROM ChatMessages
                WHERE ConversationId = $conv
                ORDER BY COALESCE(RevealedAt, CreatedAt) DESC, CreatedAt DESC
                LIMIT $limit
            )
            ORDER BY COALESCE(RevealedAt, CreatedAt) ASC, CreatedAt ASC;
            """;
        cmd.Parameters.AddWithValue("$conv", conversationId);
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<ChatMessageRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sourcesJson = reader.GetString(7);
            var notifJson = reader.IsDBNull(8) ? null : reader.GetString(8);

            list.Add(new ChatMessageRecord
            {
                Id = reader.GetString(0),
                ConversationId = reader.GetString(1),
                Role = reader.GetString(2),
                Content = reader.GetString(3),
                StatusText = reader.IsDBNull(4) ? null : reader.GetString(4),
                IsStreaming = reader.GetInt32(5) == 1,
                TaskId = reader.IsDBNull(6) ? null : reader.GetString(6),
                Sources = JsonSerializer.Deserialize<List<MailSourceRef>>(sourcesJson) ?? new(),
                Notification = notifJson is null ? null : JsonSerializer.Deserialize<NotificationActionPayload>(notifJson),
                CreatedAt = DateTimeOffset.Parse(reader.GetString(10)),
                ReplyToMessageId = reader.IsDBNull(11) ? null : reader.GetString(11),
                RevealedAt = reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12))
            });
        }
        return list;
    }

    public async Task<int> ClearImageAttachmentsInHistoryAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        var toUpdate = new List<(string Id, string Content)>();

        await using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.CommandText = "SELECT Id, Content FROM ChatMessages WHERE Content LIKE '%[Attachment:%';";
            await using var reader = await selectCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                var content = reader.GetString(1);
                var cleaned = System.Text.RegularExpressions.Regex.Replace(
                    content,
                    @"\[Attachment:\s*([^\]]+)\]",
                    match =>
                    {
                        var path = match.Groups[1].Value.Trim();
                        return $"[Cleared attachment: {Path.GetFileName(path)}]";
                    });
                if (!string.Equals(cleaned, content, StringComparison.Ordinal))
                    toUpdate.Add((id, cleaned));
            }
        }

        foreach (var (id, content) in toUpdate)
        {
            await using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE ChatMessages SET Content = $content WHERE Id = $id;";
            upd.Parameters.AddWithValue("$content", content);
            upd.Parameters.AddWithValue("$id", id);
            await upd.ExecuteNonQueryAsync(ct);
        }

        return toUpdate.Count;
    }

    public async Task<int> ClearChatMessagesAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM ChatMessages;";
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> TryRecordMailEventAsync(MailEventRecord ev, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO MailEvents (EventId, MailId, RuleDecision, Processed, DetectedAt)
            VALUES ($eid, $mid, $dec, $proc, $det);
            """;
        cmd.Parameters.AddWithValue("$eid", ev.EventId);
        cmd.Parameters.AddWithValue("$mid", ev.MailId);
        cmd.Parameters.AddWithValue("$dec", ev.RuleDecision);
        cmd.Parameters.AddWithValue("$proc", ev.Processed ? 1 : 0);
        cmd.Parameters.AddWithValue("$det", ev.DetectedAt.ToString("O"));
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    public async Task<bool> TryCreateNotificationAsync(string conversationId, NotificationActionPayload payload, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO Notifications (NotificationId, EventId, MailId, ConversationId, Title, Summary, Status, CreatedAt)
            VALUES ($nid, $eid, $mid, $conv, $title, $sum, $status, $created);
            """;
        cmd.Parameters.AddWithValue("$nid", payload.NotificationId);
        cmd.Parameters.AddWithValue("$eid", payload.EventId);
        cmd.Parameters.AddWithValue("$mid", payload.MailId);
        cmd.Parameters.AddWithValue("$conv", conversationId);
        cmd.Parameters.AddWithValue("$title", payload.Title);
        cmd.Parameters.AddWithValue("$sum", payload.Summary);
        cmd.Parameters.AddWithValue("$status", payload.Status);
        cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    public async Task UpdateNotificationStatusAsync(string notificationId, string status, DateTimeOffset? snoozeUntil = null, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE Notifications SET Status = $status, SnoozeUntil = $snooze WHERE NotificationId = $nid;
            """;
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$snooze", snoozeUntil.HasValue ? snoozeUntil.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$nid", notificationId);
        await cmd.ExecuteNonQueryAsync(ct);

        // Also update embedded notification payload in ChatMessages
        await using var findCmd = conn.CreateCommand();
        findCmd.CommandText = "SELECT Id, NotificationJson FROM ChatMessages WHERE Role = 'notification' AND NotificationJson LIKE $pattern;";
        findCmd.Parameters.AddWithValue("$pattern", $"%{notificationId}%");
        await using var reader = await findCmd.ExecuteReaderAsync(ct);
        var updates = new List<(string MsgId, string NewJson)>();
        while (await reader.ReadAsync(ct))
        {
            var msgId = reader.GetString(0);
            var json = reader.GetString(1);
            var payload = JsonSerializer.Deserialize<NotificationActionPayload>(json);
            if (payload is not null && payload.NotificationId == notificationId)
            {
                var updated = payload with { Status = status };
                updates.Add((msgId, JsonSerializer.Serialize(updated)));
            }
        }
        await reader.CloseAsync();

        foreach (var (msgId, newJson) in updates)
        {
            await using var upCmd = conn.CreateCommand();
            upCmd.CommandText = "UPDATE ChatMessages SET NotificationJson = $json WHERE Id = $id;";
            upCmd.Parameters.AddWithValue("$json", newJson);
            upCmd.Parameters.AddWithValue("$id", msgId);
            await upCmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<List<NotificationActionPayload>> GetDueSnoozedNotificationsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT NotificationId, EventId, MailId, Title, Summary, Status, SnoozeUntil
            FROM Notifications
            WHERE Status = 'Snoozed' AND SnoozeUntil IS NOT NULL;
            """;
        var due = new List<NotificationActionPayload>();
        var now = DateTimeOffset.UtcNow;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (DateTimeOffset.TryParse(reader.GetString(6), out var until) && until <= now)
            {
                due.Add(new NotificationActionPayload(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    "Unread"));
            }
        }
        return due;
    }

    public async Task UpsertTaskAsync(AgentTaskRecord task, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AgentTasks (TaskId, ConversationId, Goal, Status, TriggerEventId, ResultSummary, CreatedAt, UpdatedAt)
            VALUES ($tid, $conv, $goal, $status, $ev, $res, $created, $updated)
            ON CONFLICT(TaskId) DO UPDATE SET
                Status = excluded.Status,
                ResultSummary = excluded.ResultSummary,
                UpdatedAt = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("$tid", task.TaskId);
        cmd.Parameters.AddWithValue("$conv", task.ConversationId);
        cmd.Parameters.AddWithValue("$goal", task.Goal);
        cmd.Parameters.AddWithValue("$status", task.Status);
        cmd.Parameters.AddWithValue("$ev", (object?)task.TriggerEventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$res", (object?)task.ResultSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$created", task.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", task.UpdatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<AgentSessionRecord?> GetActiveSessionAsync(string conversationId, string roleName, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SessionId, ConversationId, RoleName, EstimatedTokens, IsActive, HandoffJson, CreatedAt
            FROM AgentSessions
            WHERE ConversationId = $conv AND RoleName = $role AND IsActive = 1
            ORDER BY CreatedAt DESC
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$conv", conversationId);
        cmd.Parameters.AddWithValue("$role", roleName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new AgentSessionRecord
        {
            SessionId = reader.GetString(0),
            ConversationId = reader.GetString(1),
            RoleName = reader.GetString(2),
            EstimatedTokens = reader.GetInt32(3),
            IsActive = reader.GetInt32(4) == 1,
            HandoffJson = reader.GetString(5),
            CreatedAt = DateTimeOffset.Parse(reader.GetString(6))
        };
    }

    public async Task SaveSessionAsync(AgentSessionRecord session, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AgentSessions (SessionId, ConversationId, RoleName, EstimatedTokens, IsActive, HandoffJson, CreatedAt)
            VALUES ($sid, $conv, $role, $tok, $active, $handoff, $created)
            ON CONFLICT(SessionId) DO UPDATE SET
                EstimatedTokens = excluded.EstimatedTokens,
                IsActive = excluded.IsActive,
                HandoffJson = excluded.HandoffJson;
            """;
        cmd.Parameters.AddWithValue("$sid", session.SessionId);
        cmd.Parameters.AddWithValue("$conv", session.ConversationId);
        cmd.Parameters.AddWithValue("$role", session.RoleName);
        cmd.Parameters.AddWithValue("$tok", session.EstimatedTokens);
        cmd.Parameters.AddWithValue("$active", session.IsActive ? 1 : 0);
        cmd.Parameters.AddWithValue("$handoff", session.HandoffJson);
        cmd.Parameters.AddWithValue("$created", session.CreatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CreateReminderAsync(ReminderRecord r, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Reminders (ReminderId, ConversationId, Title, Message, RelatedMailId, RemindAt, Status, IntervalMinutes, SkillName, SkillArgsJson, WorkdaysOnly, ExceptionDatesJson, LinkedEventId, RecurrenceRule)
            VALUES ($rid, $conv, $title, $msg, $mid, $at, $status, $interval, $skill, $args, $workdays, $exdates, $linked, $rrule);
            """;
        cmd.Parameters.AddWithValue("$rid", r.ReminderId);
        cmd.Parameters.AddWithValue("$conv", r.ConversationId);
        cmd.Parameters.AddWithValue("$title", r.Title);
        cmd.Parameters.AddWithValue("$msg", r.Message);
        cmd.Parameters.AddWithValue("$mid", (object?)r.RelatedMailId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", r.RemindAt.ToString("O"));
        cmd.Parameters.AddWithValue("$status", r.Status);
        cmd.Parameters.AddWithValue("$interval", r.IntervalMinutes);
        cmd.Parameters.AddWithValue("$skill", (object?)r.SkillName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$args", (object?)r.SkillArgsJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$workdays", r.WorkdaysOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("$exdates", string.IsNullOrWhiteSpace(r.ExceptionDatesJson) ? "[]" : r.ExceptionDatesJson);
        cmd.Parameters.AddWithValue("$linked", (object?)r.LinkedEventId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rrule", (object?)r.RecurrenceRule ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<ReminderRecord>> GetDueRemindersAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ReminderId, ConversationId, Title, Message, RelatedMailId, RemindAt, Status, IntervalMinutes, SkillName, SkillArgsJson, WorkdaysOnly, ExceptionDatesJson, LinkedEventId, RecurrenceRule
            FROM Reminders
            WHERE Status = 'Pending';
            """;
        var due = new List<ReminderRecord>();
        var now = DateTimeOffset.UtcNow;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (DateTimeOffset.TryParse(reader.GetString(5), out var at) && at <= now)
            {
                due.Add(new ReminderRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    at,
                    reader.GetString(6),
                    reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    !reader.IsDBNull(10) && reader.GetInt32(10) != 0,
                    reader.IsDBNull(11) ? "[]" : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13)));
            }
        }
        return due;
    }

    public async Task<List<ReminderRecord>> GetPendingRemindersAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ReminderId, ConversationId, Title, Message, RelatedMailId, RemindAt, Status, IntervalMinutes, SkillName, SkillArgsJson, WorkdaysOnly, ExceptionDatesJson, LinkedEventId, RecurrenceRule
            FROM Reminders
            WHERE Status = 'Pending'
            ORDER BY RemindAt ASC;
            """;
        var list = new List<ReminderRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (DateTimeOffset.TryParse(reader.GetString(5), out var at))
            {
                list.Add(new ReminderRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    at,
                    reader.GetString(6),
                    reader.GetInt32(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    !reader.IsDBNull(10) && reader.GetInt32(10) != 0,
                    reader.IsDBNull(11) ? "[]" : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13)));
            }
        }
        return list;
    }

    public async Task DeleteReminderAsync(string reminderId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Reminders WHERE ReminderId = $rid;";
        cmd.Parameters.AddWithValue("$rid", reminderId);
        await cmd.ExecuteNonQueryAsync(ct);
        await DeleteAnnotationsForTargetAsync(conn, "schedule", reminderId, ct);
    }

    public async Task RescheduleReminderAsync(string reminderId, DateTimeOffset nextAt, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Reminders SET RemindAt = $at WHERE ReminderId = $rid;";
        cmd.Parameters.AddWithValue("$at", nextAt.ToString("O"));
        cmd.Parameters.AddWithValue("$rid", reminderId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> UpdateReminderAsync(
        string reminderId,
        string? title = null,
        string? message = null,
        DateTimeOffset? remindAt = null,
        int? intervalMinutes = null,
        string? skillName = null,
        string? skillArgsJson = null,
        CancellationToken ct = default)
    {
        var sets = new List<string>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("$rid", reminderId);

        if (title is not null)
        {
            sets.Add("Title = $title");
            cmd.Parameters.AddWithValue("$title", title);
        }
        if (message is not null)
        {
            sets.Add("Message = $msg");
            cmd.Parameters.AddWithValue("$msg", message);
        }
        if (remindAt.HasValue)
        {
            sets.Add("RemindAt = $at");
            cmd.Parameters.AddWithValue("$at", remindAt.Value.ToString("O"));
        }
        if (intervalMinutes.HasValue)
        {
            sets.Add("IntervalMinutes = $iv");
            cmd.Parameters.AddWithValue("$iv", Math.Max(0, intervalMinutes.Value));
        }
        if (skillName is not null)
        {
            sets.Add("SkillName = $sk");
            cmd.Parameters.AddWithValue("$sk", string.IsNullOrWhiteSpace(skillName) ? DBNull.Value : skillName);
        }
        if (skillArgsJson is not null)
        {
            sets.Add("SkillArgsJson = $sa");
            cmd.Parameters.AddWithValue("$sa", string.IsNullOrWhiteSpace(skillArgsJson) ? DBNull.Value : skillArgsJson);
        }

        if (sets.Count == 0) return false;

        cmd.CommandText = $"UPDATE Reminders SET {string.Join(", ", sets)} WHERE ReminderId = $rid;";
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<List<SkillRecord>> GetSkillsAsync(string? kind = null, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(kind))
        {
            cmd.CommandText = """
                SELECT Name, Kind, HandlerType, DisplayDescription, MainAgentSpec, PlannerSpec, Command, AliasesJson, Capability, IsSilentPoller, IsEnabled, CreatedAt, UpdatedAt
                FROM Skills
                ORDER BY Kind ASC, Name ASC;
                """;
        }
        else
        {
            cmd.CommandText = """
                SELECT Name, Kind, HandlerType, DisplayDescription, MainAgentSpec, PlannerSpec, Command, AliasesJson, Capability, IsSilentPoller, IsEnabled, CreatedAt, UpdatedAt
                FROM Skills
                WHERE Kind = $kind
                ORDER BY CreatedAt ASC, Name ASC;
                """;
            cmd.Parameters.AddWithValue("$kind", kind.Trim());
        }

        var list = new List<SkillRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var aliasesJson = r.IsDBNull(7) ? "[]" : r.GetString(7);
            List<string> aliases;
            try { aliases = JsonSerializer.Deserialize<List<string>>(aliasesJson) ?? new List<string>(); }
            catch { aliases = new List<string>(); }

            list.Add(new SkillRecord(
                Name: r.GetString(0),
                Kind: r.GetString(1),
                HandlerType: r.IsDBNull(2) ? null : r.GetString(2),
                DisplayDescription: r.GetString(3),
                MainAgentSpec: r.GetString(4),
                PlannerSpec: r.GetString(5),
                Command: r.IsDBNull(6) ? null : r.GetString(6),
                Aliases: aliases,
                Capability: r.IsDBNull(8) ? null : r.GetString(8),
                IsSilentPoller: r.GetInt32(9) != 0,
                IsEnabled: r.GetInt32(10) != 0,
                CreatedAt: DateTimeOffset.Parse(r.GetString(11)),
                UpdatedAt: DateTimeOffset.Parse(r.GetString(12))));
        }
        return list;
    }

    public async Task UpsertSkillAsync(SkillRecord skill, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO Skills (Name, Kind, HandlerType, DisplayDescription, MainAgentSpec, PlannerSpec, Command, AliasesJson, Capability, IsSilentPoller, IsEnabled, CreatedAt, UpdatedAt)
            VALUES ($name, $kind, $handler, $desc, $mspec, $pspec, $cmd, $aliases, $cap, $poller, $enabled, $created, $updated)
            ON CONFLICT(Name) DO UPDATE SET
                Kind = excluded.Kind,
                HandlerType = excluded.HandlerType,
                DisplayDescription = excluded.DisplayDescription,
                MainAgentSpec = excluded.MainAgentSpec,
                PlannerSpec = excluded.PlannerSpec,
                Command = excluded.Command,
                AliasesJson = excluded.AliasesJson,
                Capability = excluded.Capability,
                IsSilentPoller = excluded.IsSilentPoller,
                UpdatedAt = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("$name", skill.Name);
        cmd.Parameters.AddWithValue("$kind", skill.Kind);
        cmd.Parameters.AddWithValue("$handler", (object?)skill.HandlerType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$desc", skill.DisplayDescription);
        cmd.Parameters.AddWithValue("$mspec", skill.MainAgentSpec);
        cmd.Parameters.AddWithValue("$pspec", skill.PlannerSpec);
        cmd.Parameters.AddWithValue("$cmd", (object?)skill.Command ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$aliases", JsonSerializer.Serialize(skill.Aliases ?? new List<string>()));
        cmd.Parameters.AddWithValue("$cap", (object?)skill.Capability ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$poller", skill.IsSilentPoller ? 1 : 0);
        cmd.Parameters.AddWithValue("$enabled", skill.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", skill.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", skill.UpdatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SyncBasicSkillsAsync(
        IReadOnlyList<SkillRecord> defaults,
        IReadOnlySet<string> legacyDisabled,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var existingBasic = new List<SkillRecord>();
        await using (var q = conn.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = """
                SELECT Name, Kind, HandlerType, DisplayDescription, MainAgentSpec, PlannerSpec, Command, AliasesJson, Capability, IsSilentPoller, IsEnabled, CreatedAt, UpdatedAt
                FROM Skills
                WHERE Kind = 'basic';
                """;
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var aliasesJson = r.IsDBNull(7) ? "[]" : r.GetString(7);
                List<string> aliases;
                try { aliases = JsonSerializer.Deserialize<List<string>>(aliasesJson) ?? new List<string>(); }
                catch { aliases = new List<string>(); }

                existingBasic.Add(new SkillRecord(
                    Name: r.GetString(0),
                    Kind: r.GetString(1),
                    HandlerType: r.IsDBNull(2) ? null : r.GetString(2),
                    DisplayDescription: r.GetString(3),
                    MainAgentSpec: r.GetString(4),
                    PlannerSpec: r.GetString(5),
                    Command: r.IsDBNull(6) ? null : r.GetString(6),
                    Aliases: aliases,
                    Capability: r.IsDBNull(8) ? null : r.GetString(8),
                    IsSilentPoller: r.GetInt32(9) != 0,
                    IsEnabled: r.GetInt32(10) != 0,
                    CreatedAt: DateTimeOffset.Parse(r.GetString(11)),
                    UpdatedAt: DateTimeOffset.Parse(r.GetString(12))));
            }
        }

        var defaultHandlers = defaults
            .Where(d => !string.IsNullOrWhiteSpace(d.HandlerType))
            .Select(d => d.HandlerType!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Remove orphaned built-in skills whose handler classes no longer exist
        foreach (var ex in existingBasic)
        {
            if (string.IsNullOrWhiteSpace(ex.HandlerType) || !defaultHandlers.Contains(ex.HandlerType))
            {
                await using var del = conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM Skills WHERE Name = $name AND Kind = 'basic';";
                del.Parameters.AddWithValue("$name", ex.Name);
                await del.ExecuteNonQueryAsync(ct);
            }
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var def in defaults)
        {
            var match = existingBasic.FirstOrDefault(e =>
                            !string.IsNullOrWhiteSpace(e.HandlerType) &&
                            string.Equals(e.HandlerType, def.HandlerType, StringComparison.OrdinalIgnoreCase))
                        ?? existingBasic.FirstOrDefault(e =>
                            string.Equals(e.Name, def.Name, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                bool disabledByLegacy = legacyDisabled.Contains(def.Name)
                    || (def.Aliases is { Count: > 0 } && def.Aliases.Any(a => legacyDisabled.Contains(a)));
                await InsertSkillRowAsync(conn, tx, def with
                {
                    IsEnabled = !disabledByLegacy,
                    CreatedAt = now,
                    UpdatedAt = now
                }, ct);
            }
            // Existing basic skill rows are left untouched so SQLite remains the single source of truth.
        }

        await tx.CommitAsync(ct);
    }

    private static async Task InsertSkillRowAsync(SqliteConnection conn, SqliteTransaction tx, SkillRecord skill, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT OR REPLACE INTO Skills (Name, Kind, HandlerType, DisplayDescription, MainAgentSpec, PlannerSpec, Command, AliasesJson, Capability, IsSilentPoller, IsEnabled, CreatedAt, UpdatedAt)
            VALUES ($name, $kind, $handler, $desc, $mspec, $pspec, $cmd, $aliases, $cap, $poller, $enabled, $created, $updated);
            """;
        cmd.Parameters.AddWithValue("$name", skill.Name);
        cmd.Parameters.AddWithValue("$kind", skill.Kind);
        cmd.Parameters.AddWithValue("$handler", (object?)skill.HandlerType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$desc", skill.DisplayDescription);
        cmd.Parameters.AddWithValue("$mspec", skill.MainAgentSpec);
        cmd.Parameters.AddWithValue("$pspec", skill.PlannerSpec);
        cmd.Parameters.AddWithValue("$cmd", (object?)skill.Command ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$aliases", JsonSerializer.Serialize(skill.Aliases ?? new List<string>()));
        cmd.Parameters.AddWithValue("$cap", (object?)skill.Capability ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$poller", skill.IsSilentPoller ? 1 : 0);
        cmd.Parameters.AddWithValue("$enabled", skill.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", skill.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", skill.UpdatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> MigrateLegacyCustomSkillsAsync(IReadOnlySet<string> legacyDisabled, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using (var chk = conn.CreateCommand())
        {
            chk.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='CustomSkills' LIMIT 1;";
            if (await chk.ExecuteScalarAsync(ct) is null)
                return 0;
        }

        var legacyRows = new List<(string Name, string Description, string Command, string CreatedAt)>();
        await using (var q = conn.CreateCommand())
        {
            q.CommandText = "SELECT Name, Description, Command, CreatedAt FROM CustomSkills;";
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                legacyRows.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }

        int inserted = 0;
        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var row in legacyRows)
        {
            await using var ins = conn.CreateCommand();
            ins.CommandText = """
                INSERT OR IGNORE INTO Skills (Name, Kind, HandlerType, DisplayDescription, MainAgentSpec, PlannerSpec, Command, AliasesJson, Capability, IsSilentPoller, IsEnabled, CreatedAt, UpdatedAt)
                VALUES ($name, 'custom', NULL, $desc, '', '', $cmd, '[]', NULL, 0, $enabled, $created, $updated);
                """;
            ins.Parameters.AddWithValue("$name", row.Name);
            ins.Parameters.AddWithValue("$desc", row.Description);
            ins.Parameters.AddWithValue("$cmd", row.Command);
            ins.Parameters.AddWithValue("$enabled", legacyDisabled.Contains(row.Name) ? 0 : 1);
            ins.Parameters.AddWithValue("$created", row.CreatedAt);
            ins.Parameters.AddWithValue("$updated", now);
            inserted += await ins.ExecuteNonQueryAsync(ct);
        }
        return inserted;
    }

    public async Task<bool> SetSkillEnabledInDbAsync(string canonicalName, bool enabled, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Skills SET IsEnabled = $en, UpdatedAt = $at WHERE lower(Name) = lower($name);";
        cmd.Parameters.AddWithValue("$en", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$name", canonicalName);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<List<CustomSkillRecord>> GetCustomSkillsAsync(CancellationToken ct = default)
    {
        var skills = await GetSkillsAsync("custom", ct);
        return skills.Select(s => new CustomSkillRecord(
            s.Name,
            s.DisplayDescription,
            s.Command ?? "",
            s.CreatedAt)).ToList();
    }

    public async Task UpsertCustomSkillAsync(CustomSkillRecord skill, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rec = new SkillRecord(
            Name: skill.Name,
            Kind: "custom",
            HandlerType: null,
            DisplayDescription: skill.Description,
            MainAgentSpec: "",
            PlannerSpec: "",
            Command: skill.Command,
            Aliases: new List<string>(),
            Capability: null,
            IsSilentPoller: false,
            IsEnabled: true,
            CreatedAt: skill.CreatedAt,
            UpdatedAt: now);
        await UpsertSkillAsync(rec, ct);
    }

    public async Task<bool> DeleteCustomSkillAsync(string name, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM Skills WHERE lower(Name) = lower($name) AND Kind = 'custom';";
        cmd.Parameters.AddWithValue("$name", name);
        var deleted = await cmd.ExecuteNonQueryAsync(ct) > 0;

        // Clean legacy table if present so re-migration cannot reintroduce it
        try
        {
            await using var chk = conn.CreateCommand();
            chk.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='CustomSkills' LIMIT 1;";
            if (await chk.ExecuteScalarAsync(ct) is not null)
            {
                await using var legacyDel = conn.CreateCommand();
                legacyDel.CommandText = "DELETE FROM CustomSkills WHERE lower(Name) = lower($name);";
                legacyDel.Parameters.AddWithValue("$name", name);
                await legacyDel.ExecuteNonQueryAsync(ct);
            }
        }
        catch { }

        return deleted;
    }

    public async Task<string?> GetSyncValueAsync(string key, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Value FROM SyncState WHERE Key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task SetSyncValueAsync(string key, string value, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SyncState (Key, Value) VALUES ($k, $v)
            ON CONFLICT(Key) DO UPDATE SET Value = excluded.Value;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertGuardAuditAsync(string skillName, string target, string action, string reason, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO GuardAuditLogs (Id, Timestamp, SkillName, Target, Action, Reason)
            VALUES ($id, $ts, $skill, $target, $action, $reason);
            """;
        cmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$skill", skillName);
        cmd.Parameters.AddWithValue("$target", target);
        cmd.Parameters.AddWithValue("$action", action);
        cmd.Parameters.AddWithValue("$reason", reason);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkReminderTriggeredAsync(string reminderId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Reminders SET Status = 'Triggered' WHERE ReminderId = $rid;";
        cmd.Parameters.AddWithValue("$rid", reminderId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<(int Messages, int MailEvents, int ActiveSessions, int Tasks)> GetDiagnosticsAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        async Task<int> CountAsync(string table, string? where = null)
        {
            await using var c = conn.CreateCommand();
            c.CommandText = $"SELECT COUNT(*) FROM {table}" + (where is null ? ";" : $" WHERE {where};");
            var res = await c.ExecuteScalarAsync(ct);
            return Convert.ToInt32(res);
        }

        return (
            await CountAsync("ChatMessages"),
            await CountAsync("MailEvents"),
            await CountAsync("AgentSessions", "IsActive = 1"),
            await CountAsync("AgentTasks")
        );
    }

    public async Task UpsertFactAsync(KnowledgeFactRecord fact, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);

        var memKey = (fact.MemoryKey ?? "").Trim().ToLowerInvariant();
        var verifiedAt = (fact.LastVerifiedAt ?? fact.UpdatedAt).ToString("O");
        var factId = fact.FactId;
        var reinforceCount = Math.Max(1, fact.ReinforceCount);
        var state = string.IsNullOrWhiteSpace(fact.State) ? "Confirmed" : fact.State.Trim();

        // Semantic MemoryKey deduplication & 2-bit reinforcement: repeating a Candidate auto-promotes to Confirmed.
        if (!string.IsNullOrWhiteSpace(memKey))
        {
            await using var findCmd = conn.CreateCommand();
            findCmd.CommandText = """
                SELECT FactId, ReinforceCount, State
                FROM KnowledgeFacts
                WHERE ConversationId = $conv AND lower(MemoryKey) = $mkey
                LIMIT 1;
                """;
            findCmd.Parameters.AddWithValue("$conv", fact.ConversationId);
            findCmd.Parameters.AddWithValue("$mkey", memKey);
            await using var r = await findCmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                factId = r.GetString(0);
                var prevCount = r.IsDBNull(1) ? 1 : r.GetInt32(1);
                var prevState = r.IsDBNull(2) ? "Confirmed" : r.GetString(2);
                reinforceCount = Math.Min(10, prevCount + 1);
                if (reinforceCount >= 2 ||
                    string.Equals(prevState, "Confirmed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(state, "Confirmed", StringComparison.OrdinalIgnoreCase))
                {
                    state = "Confirmed";
                }
            }
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO KnowledgeFacts (FactId, ConversationId, MemoryKey, Category, Summary, Detail, Evidence, Confidence, State, ReinforceCount, ExpiresAt, LastVerifiedAt, TagsJson, CreatedAt, UpdatedAt)
            VALUES ($id, $conv, $mkey, $cat, $sum, $det, $ev, $conf, $state, $rcnt, $exp, $ver, $tags, $created, $updated)
            ON CONFLICT(FactId) DO UPDATE SET
                MemoryKey = CASE WHEN excluded.MemoryKey <> '' THEN excluded.MemoryKey ELSE KnowledgeFacts.MemoryKey END,
                Category = excluded.Category,
                Summary = excluded.Summary,
                Detail = excluded.Detail,
                Evidence = excluded.Evidence,
                Confidence = excluded.Confidence,
                State = excluded.State,
                ReinforceCount = excluded.ReinforceCount,
                ExpiresAt = excluded.ExpiresAt,
                LastVerifiedAt = excluded.LastVerifiedAt,
                TagsJson = excluded.TagsJson,
                UpdatedAt = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("$id", factId);
        cmd.Parameters.AddWithValue("$conv", fact.ConversationId);
        cmd.Parameters.AddWithValue("$mkey", memKey);
        cmd.Parameters.AddWithValue("$cat", fact.Category);
        cmd.Parameters.AddWithValue("$sum", fact.Summary);
        cmd.Parameters.AddWithValue("$det", fact.Detail);
        cmd.Parameters.AddWithValue("$ev", (object?)fact.Evidence ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$conf", fact.Confidence);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$rcnt", reinforceCount);
        cmd.Parameters.AddWithValue("$exp", fact.ExpiresAt.HasValue ? fact.ExpiresAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("$ver", verifiedAt);
        cmd.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(fact.Tags));
        cmd.Parameters.AddWithValue("$created", fact.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", fact.UpdatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> PruneExpiredAndStaleCandidateFactsAsync(string conversationId, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM KnowledgeFacts
            WHERE ConversationId = $conv
              AND (
                    (ExpiresAt IS NOT NULL AND ExpiresAt <= $now)
                 OR (State = 'Candidate' AND ReinforceCount < 2 AND COALESCE(LastVerifiedAt, UpdatedAt) < $cutoff)
              );
            """;
        cmd.Parameters.AddWithValue("$conv", conversationId);
        cmd.Parameters.AddWithValue("$now", nowUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$cutoff", nowUtc.AddDays(-14).ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<FactSummaryItem>> ListFactsAsync(
        string conversationId,
        string? keyword = null,
        string? category = null,
        int limit = 20,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        var clauses = new List<string> { "ConversationId = $conv" };
        cmd.Parameters.AddWithValue("$conv", conversationId);

        if (!string.IsNullOrWhiteSpace(category))
        {
            clauses.Add("lower(Category) = lower($cat)");
            cmd.Parameters.AddWithValue("$cat", category.Trim());
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            clauses.Add("(Summary LIKE $kw OR Detail LIKE $kw OR TagsJson LIKE $kw OR COALESCE(Evidence, '') LIKE $kw)");
            cmd.Parameters.AddWithValue("$kw", $"%{keyword.Trim()}%");
        }

        cmd.CommandText = $"""
            SELECT FactId, Category, Summary, Confidence, CreatedAt
            FROM KnowledgeFacts
            WHERE {string.Join(" AND ", clauses)}
            ORDER BY UpdatedAt DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));

        var list = new List<FactSummaryItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new FactSummaryItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                DateTimeOffset.Parse(reader.GetString(4))));
        }
        return list;
    }

    public async Task<List<FactSummaryItem>> RecallRelevantFactsAsync(
        string conversationId,
        string userPrompt,
        int limit = 5,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT FactId, Category, Summary, TagsJson, Confidence, CreatedAt, COALESCE(MemoryKey, ''), COALESCE(State, 'Confirmed'), COALESCE(ReinforceCount, 1)
            FROM KnowledgeFacts
            WHERE ConversationId = $conv
              AND COALESCE(State, 'Confirmed') = 'Confirmed'
              AND (ExpiresAt IS NULL OR ExpiresAt > $now)
            ORDER BY UpdatedAt DESC
            LIMIT 100;
            """;
        cmd.Parameters.AddWithValue("$conv", conversationId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        var candidates = new List<(FactSummaryItem Item, string TagsText)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var item = new FactSummaryItem(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5)),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8));
            var tagsJson = reader.IsDBNull(3) ? "[]" : reader.GetString(3);
            candidates.Add((item, tagsJson));
        }

        if (candidates.Count == 0)
            return new List<FactSummaryItem>();

        var query = userPrompt.Trim();
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.Split(new[] { ' ', '\t', '\r', '\n', '，', '。', '？', '！', '、', ',', '.', '?', '!', ':', '：', '(', ')', '（', '）' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var clean = part.Trim();
            if (clean.Length >= 2)
                tokens.Add(clean);
            if (clean.Length > 2 && clean.Any(c => c > 127))
            {
                for (int i = 0; i <= clean.Length - 2; i++)
                    tokens.Add(clean.Substring(i, 2));
            }
        }

        var scored = candidates
            .Select((c, idx) =>
            {
                var haystack = $"{c.Item.Summary} {c.TagsText} {c.Item.Category}";
                int score = 0;
                foreach (var tk in tokens)
                {
                    if (haystack.Contains(tk, StringComparison.OrdinalIgnoreCase))
                        score += tk.Length >= 3 ? 3 : 1;
                }
                return (c.Item, Score: score, RecencyRank: idx);
            })
            .ToList();

        var matched = scored
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.RecencyRank)
            .Take(limit)
            .Select(x => x.Item)
            .ToList();

        if (matched.Count > 0)
            return matched;

        return candidates.Take(Math.Min(3, limit)).Select(c => c.Item).ToList();
    }

    public async Task<KnowledgeFactRecord?> GetFactDetailAsync(string factId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT FactId, ConversationId, Category, Summary, Detail, Evidence, Confidence, TagsJson, CreatedAt, UpdatedAt
            FROM KnowledgeFacts
            WHERE FactId = $id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", factId.Trim());

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var tagsJson = reader.IsDBNull(7) ? "[]" : reader.GetString(7);
        var tags = JsonSerializer.Deserialize<List<string>>(tagsJson) ?? new List<string>();

        return new KnowledgeFactRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetString(6),
            tags,
            DateTimeOffset.Parse(reader.GetString(8)),
            DateTimeOffset.Parse(reader.GetString(9)));
    }

    public async Task<bool> DeleteFactAsync(string factId, CancellationToken ct = default)
    {
        var trimmed = factId.Trim();
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM KnowledgeFacts WHERE FactId = $id;";
        cmd.Parameters.AddWithValue("$id", trimmed);
        var deleted = await cmd.ExecuteNonQueryAsync(ct) > 0;
        if (deleted)
            await DeleteAnnotationsForTargetAsync(conn, "fact", trimmed, ct);
        return deleted;
    }

    public async Task<List<KnowledgeFactRecord>> GetUserPrefFactsAsync(
        string conversationId,
        int limit = 20,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT FactId, ConversationId, Category, Summary, Detail, Evidence, Confidence, TagsJson, CreatedAt, UpdatedAt,
                   COALESCE(MemoryKey, ''), COALESCE(State, 'Confirmed'), COALESCE(ReinforceCount, 1), ExpiresAt, LastVerifiedAt
            FROM KnowledgeFacts
            WHERE ConversationId = $conv
              AND Category = 'user_pref'
              AND COALESCE(State, 'Confirmed') = 'Confirmed'
              AND (ExpiresAt IS NULL OR ExpiresAt > $now)
            ORDER BY UpdatedAt DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$conv", conversationId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50));

        var list = new List<KnowledgeFactRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var tagsJson = reader.IsDBNull(7) ? "[]" : reader.GetString(7);
            var tags = JsonSerializer.Deserialize<List<string>>(tagsJson) ?? new List<string>();
            list.Add(new KnowledgeFactRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                tags,
                DateTimeOffset.Parse(reader.GetString(8)),
                DateTimeOffset.Parse(reader.GetString(9)),
                MemoryKey: reader.GetString(10),
                State: reader.GetString(11),
                ReinforceCount: reader.GetInt32(12),
                ExpiresAt: reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)),
                LastVerifiedAt: reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14))));
        }
        return list;
    }

    /// <summary>
    /// Returns Confirmed, non-expired preference/proactive-topic facts (newest first) for proactive awareness overrides
    /// and standing user-requested idle topics, never including unconfirmed Candidates.
    /// </summary>
    public async Task<List<KnowledgeFactRecord>> GetConfirmedProactivePrefsAsync(
        string conversationId,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT FactId, ConversationId, Category, Summary, Detail, Evidence, Confidence, TagsJson, CreatedAt, UpdatedAt,
                   COALESCE(MemoryKey, ''), COALESCE(State, 'Confirmed'), COALESCE(ReinforceCount, 1), ExpiresAt, LastVerifiedAt
            FROM KnowledgeFacts
            WHERE ConversationId = $conv
              AND lower(Category) IN ('user_pref', 'proactive_topic')
              AND COALESCE(State, 'Confirmed') = 'Confirmed'
              AND (ExpiresAt IS NULL OR ExpiresAt > $now)
            ORDER BY UpdatedAt DESC
            LIMIT 50;
            """;
        cmd.Parameters.AddWithValue("$conv", conversationId);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));

        var list = new List<KnowledgeFactRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var tagsJson = reader.IsDBNull(7) ? "[]" : reader.GetString(7);
            var tags = JsonSerializer.Deserialize<List<string>>(tagsJson) ?? new List<string>();
            list.Add(new KnowledgeFactRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                tags,
                DateTimeOffset.Parse(reader.GetString(8)),
                DateTimeOffset.Parse(reader.GetString(9)),
                MemoryKey: reader.GetString(10),
                State: reader.GetString(11),
                ReinforceCount: reader.GetInt32(12),
                ExpiresAt: reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)),
                LastVerifiedAt: reader.IsDBNull(14) ? null : DateTimeOffset.Parse(reader.GetString(14))));
        }
        return list;
    }

    /// <summary>
    /// Returns up to <paramref name="limit"/> recent user-authored messages since <paramref name="sinceUtc"/>
    /// in chronological order, strictly excluding all assistant messages to prevent proactive self-imitation.
    /// </summary>
    public async Task<List<string>> GetRecentUserOnlyMessagesAsync(
        string conversationId,
        DateTimeOffset sinceUtc,
        int limit = 3,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Content
            FROM (
                SELECT Content, CreatedAt, RevealedAt
                FROM ChatMessages
                WHERE ConversationId = $conv
                  AND Role = 'user'
                  AND COALESCE(RevealedAt, CreatedAt) >= $since
                  AND trim(COALESCE(Content, '')) <> ''
                ORDER BY COALESCE(RevealedAt, CreatedAt) DESC, CreatedAt DESC
                LIMIT $limit
            )
            ORDER BY COALESCE(RevealedAt, CreatedAt) ASC, CreatedAt ASC;
            """;
        cmd.Parameters.AddWithValue("$conv", conversationId);
        cmd.Parameters.AddWithValue("$since", sinceUtc.ToString("O"));
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10));

        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var c = reader.GetString(0)?.Trim();
            if (!string.IsNullOrWhiteSpace(c))
                list.Add(c);
        }
        return list;
    }

    public async Task<List<KnowledgeFactRecord>> RecallRelevantUserPrefFactsAsync(
        string conversationId,
        string? query,
        int limit = 5,
        CancellationToken ct = default)
    {
        var all = await GetUserPrefFactsAsync(conversationId, 50, ct);
        if (all.Count == 0 || string.IsNullOrWhiteSpace(query))
            return all.Take(limit).ToList();

        var qTokens = TokenizeForPrefMatch(query);
        if (qTokens.Count == 0)
            return all.Take(limit).ToList();

        var alwaysTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "always", "global", "tone", "reply" };
        return all.Select(f =>
        {
            var isAlways = f.Tags.Any(t => alwaysTags.Contains(t));
            var fTokens = TokenizeForPrefMatch($"{f.Summary} {f.Detail} {string.Join(" ", f.Tags)}");
            int overlap = qTokens.Count(t => fTokens.Contains(t));
            return (Fact: f, Score: overlap + (isAlways ? 1 : 0));
        })
        .Where(x => x.Score > 0)
        .OrderByDescending(x => x.Score)
        .ThenByDescending(x => x.Fact.UpdatedAt)
        .Take(Math.Clamp(limit, 1, 15))
        .Select(x => x.Fact)
        .ToList();
    }

    private static HashSet<string> TokenizeForPrefMatch(string text)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return set;

        foreach (var part in System.Text.RegularExpressions.Regex.Split(text.ToLowerInvariant(), @"[\s\p{P}\p{S}]+"))
        {
            if (part.Length >= 2)
                set.Add(part);
            if (part.Any(c => c >= 0x4E00 && c <= 0x9FFF))
            {
                for (int i = 0; i < part.Length - 1; i++)
                    set.Add(part.Substring(i, 2));
            }
        }
        return set;
    }

    public async Task<bool> UpdateFactAsync(
        string factId,
        string? summary = null,
        string? detail = null,
        string? category = null,
        string? evidence = null,
        CancellationToken ct = default)
    {
        var sets = new List<string> { "UpdatedAt = $updated" };
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("$id", factId.Trim());
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));

        if (summary is not null)
        {
            sets.Add("Summary = $sum");
            cmd.Parameters.AddWithValue("$sum", summary);
        }
        if (detail is not null)
        {
            sets.Add("Detail = $det");
            cmd.Parameters.AddWithValue("$det", detail);
        }
        if (category is not null)
        {
            sets.Add("Category = $cat");
            cmd.Parameters.AddWithValue("$cat", category);
        }
        if (evidence is not null)
        {
            sets.Add("Evidence = $ev");
            cmd.Parameters.AddWithValue("$ev", evidence);
        }

        if (sets.Count == 1) return false;
        cmd.CommandText = $"UPDATE KnowledgeFacts SET {string.Join(", ", sets)} WHERE FactId = $id;";
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task CreateCalendarEventAsync(CalendarEventRecord ev, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO CalendarEvents (EventId, ConversationId, Title, Category, StartAt, EndAt, AllDay, Notes, CreatedAt, UpdatedAt, Recurrence, ExceptionDatesJson)
            VALUES ($id, $conv, $title, $cat, $start, $end, $all, $notes, $created, $updated, $rec, $exdates);
            """;
        cmd.Parameters.AddWithValue("$id", ev.EventId);
        cmd.Parameters.AddWithValue("$conv", ev.ConversationId);
        cmd.Parameters.AddWithValue("$title", ev.Title);
        cmd.Parameters.AddWithValue("$cat", ev.Category);
        cmd.Parameters.AddWithValue("$start", ev.StartAt.ToString("O"));
        cmd.Parameters.AddWithValue("$end", ev.EndAt.ToString("O"));
        cmd.Parameters.AddWithValue("$all", ev.AllDay ? 1 : 0);
        cmd.Parameters.AddWithValue("$notes", ev.Notes);
        cmd.Parameters.AddWithValue("$created", ev.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$updated", ev.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("$rec", string.IsNullOrWhiteSpace(ev.Recurrence) ? "none" : ev.Recurrence);
        cmd.Parameters.AddWithValue("$exdates", string.IsNullOrWhiteSpace(ev.ExceptionDatesJson) ? "[]" : ev.ExceptionDatesJson);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<CalendarEventRecord>> GetCalendarEventsAsync(
        string conversationId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT EventId, ConversationId, Title, Category, StartAt, EndAt, AllDay, Notes, CreatedAt, UpdatedAt, Recurrence, ExceptionDatesJson
            FROM CalendarEvents
            WHERE ConversationId = $conv
            ORDER BY StartAt ASC;
            """;
        cmd.Parameters.AddWithValue("$conv", conversationId);

        var raw = new List<CalendarEventRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!DateTimeOffset.TryParse(reader.GetString(4), out var startAt)) continue;
            if (!DateTimeOffset.TryParse(reader.GetString(5), out var endAt)) continue;

            raw.Add(new CalendarEventRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                startAt,
                endAt,
                reader.GetInt32(6) != 0,
                reader.IsDBNull(7) ? "" : reader.GetString(7),
                DateTimeOffset.Parse(reader.GetString(8)),
                DateTimeOffset.Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? "none" : reader.GetString(10),
                reader.IsDBNull(11) ? "[]" : reader.GetString(11)));
        }

        if (!from.HasValue && !to.HasValue)
            return raw;

        var winStart = from ?? DateTimeOffset.MinValue;
        var winEnd = to ?? winStart.AddDays(30);
        var expanded = new List<CalendarEventRecord>();

        foreach (var ev in raw)
        {
            var exSet = ParseExceptionDates(ev.ExceptionDatesJson);
            if (!AiSecretary.Desktop.Services.RecurrenceHelper.IsRecurring(ev.Recurrence))
            {
                if (ev.EndAt >= winStart && ev.StartAt <= winEnd && !IsEntireWindowSkippedByExceptions(ev, winStart, winEnd, exSet))
                    expanded.Add(ev);
                continue;
            }

            var duration = ev.EndAt - ev.StartAt;
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
            foreach (var occStart in AiSecretary.Desktop.Services.RecurrenceHelper.ExpandOccurrences(
                ev.StartAt, ev.Recurrence, duration, winStart, winEnd, exSet))
            {
                expanded.Add(ev with { StartAt = occStart, EndAt = occStart + duration });
            }
        }

        return expanded.OrderBy(e => e.StartAt).ToList();
    }

    private static HashSet<string> ParseExceptionDates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(json);
            return arr is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(arr.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()), StringComparer.Ordinal);
        }
        catch
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static bool IsEntireWindowSkippedByExceptions(CalendarEventRecord ev, DateTimeOffset winStart, DateTimeOffset winEnd, HashSet<string> exSet)
    {
        if (exSet.Count == 0) return false;
        var dStart = (winStart > ev.StartAt ? winStart : ev.StartAt).ToLocalTime().Date;
        var dEnd = (winEnd < ev.EndAt ? winEnd : ev.EndAt).ToLocalTime().Date;
        if (dEnd < dStart) return false;
        for (var d = dStart; d <= dEnd; d = d.AddDays(1))
        {
            if (!exSet.Contains(d.ToString("yyyy-MM-dd")))
                return false;
        }
        return true;
    }

    public async Task<List<CalendarEventRecord>> FindConflictingEventsAsync(
        string conversationId,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        string? excludeEventId = null,
        CancellationToken ct = default)
    {
        var expanded = await GetCalendarEventsAsync(conversationId, startAt, endAt, ct);
        return expanded.Where(e =>
            (string.IsNullOrWhiteSpace(excludeEventId) || !e.EventId.Equals(excludeEventId, StringComparison.OrdinalIgnoreCase))
            && e.StartAt < endAt
            && e.EndAt > startAt).ToList();
    }

    public async Task<bool> SkipCalendarEventDateAsync(string eventId, string dateYmd, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        string? curJson = null;
        await using (var sel = conn.CreateCommand())
        {
            sel.CommandText = "SELECT ExceptionDatesJson FROM CalendarEvents WHERE EventId = $id LIMIT 1;";
            sel.Parameters.AddWithValue("$id", eventId.Trim());
            var obj = await sel.ExecuteScalarAsync(ct);
            if (obj is null) return false;
            curJson = obj as string;
        }

        var set = ParseExceptionDates(curJson);
        set.Add(dateYmd.Trim());
        var nextJson = JsonSerializer.Serialize(set.OrderBy(x => x).ToList());

        await using (var upd = conn.CreateCommand())
        {
            upd.CommandText = "UPDATE CalendarEvents SET ExceptionDatesJson = $ex, UpdatedAt = $u WHERE EventId = $id;";
            upd.Parameters.AddWithValue("$ex", nextJson);
            upd.Parameters.AddWithValue("$u", DateTimeOffset.UtcNow.ToString("O"));
            upd.Parameters.AddWithValue("$id", eventId.Trim());
            await upd.ExecuteNonQueryAsync(ct);
        }

        // Sync exception date to any linked reminders
        await using (var rSel = conn.CreateCommand())
        {
            rSel.CommandText = "SELECT ReminderId, ExceptionDatesJson FROM Reminders WHERE LinkedEventId = $id;";
            rSel.Parameters.AddWithValue("$id", eventId.Trim());
            var linked = new List<(string Rid, string Ex)>();
            await using var reader = await rSel.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                linked.Add((reader.GetString(0), reader.IsDBNull(1) ? "[]" : reader.GetString(1)));

            foreach (var (rid, rex) in linked)
            {
                var rset = ParseExceptionDates(rex);
                rset.Add(dateYmd.Trim());
                await using var rUpd = conn.CreateCommand();
                rUpd.CommandText = "UPDATE Reminders SET ExceptionDatesJson = $ex WHERE ReminderId = $rid;";
                rUpd.Parameters.AddWithValue("$ex", JsonSerializer.Serialize(rset.OrderBy(x => x).ToList()));
                rUpd.Parameters.AddWithValue("$rid", rid);
                await rUpd.ExecuteNonQueryAsync(ct);
            }
        }
        return true;
    }

    public async Task<bool> UpdateCalendarEventAsync(
        string eventId,
        string? title = null,
        string? category = null,
        DateTimeOffset? startAt = null,
        DateTimeOffset? endAt = null,
        bool? allDay = null,
        string? notes = null,
        string? recurrence = null,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        DateTimeOffset? oldStart = null;
        if (startAt.HasValue)
        {
            await using var sel = conn.CreateCommand();
            sel.CommandText = "SELECT StartAt FROM CalendarEvents WHERE EventId = $id LIMIT 1;";
            sel.Parameters.AddWithValue("$id", eventId.Trim());
            if (await sel.ExecuteScalarAsync(ct) is string s && DateTimeOffset.TryParse(s, out var parsedOld))
                oldStart = parsedOld;
        }

        var sets = new List<string> { "UpdatedAt = $updated" };
        await using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("$id", eventId.Trim());
        cmd.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));

        if (title is not null)
        {
            sets.Add("Title = $title");
            cmd.Parameters.AddWithValue("$title", title);
        }
        if (category is not null)
        {
            sets.Add("Category = $cat");
            cmd.Parameters.AddWithValue("$cat", category);
        }
        if (startAt.HasValue)
        {
            sets.Add("StartAt = $start");
            cmd.Parameters.AddWithValue("$start", startAt.Value.ToString("O"));
        }
        if (endAt.HasValue)
        {
            sets.Add("EndAt = $end");
            cmd.Parameters.AddWithValue("$end", endAt.Value.ToString("O"));
        }
        if (allDay.HasValue)
        {
            sets.Add("AllDay = $all");
            cmd.Parameters.AddWithValue("$all", allDay.Value ? 1 : 0);
        }
        if (notes is not null)
        {
            sets.Add("Notes = $notes");
            cmd.Parameters.AddWithValue("$notes", notes);
        }
        if (recurrence is not null)
        {
            sets.Add("Recurrence = $rec");
            cmd.Parameters.AddWithValue("$rec", string.IsNullOrWhiteSpace(recurrence) ? "none" : recurrence.Trim().ToLowerInvariant());
        }

        if (sets.Count == 1) return false;
        cmd.CommandText = $"UPDATE CalendarEvents SET {string.Join(", ", sets)} WHERE EventId = $id;";
        var ok = await cmd.ExecuteNonQueryAsync(ct) > 0;

        if (ok && startAt.HasValue && oldStart.HasValue)
        {
            var delta = startAt.Value - oldStart.Value;
            if (delta != TimeSpan.Zero)
            {
                await using var rSel = conn.CreateCommand();
                rSel.CommandText = "SELECT ReminderId, RemindAt FROM Reminders WHERE LinkedEventId = $id AND Status = 'Pending';";
                rSel.Parameters.AddWithValue("$id", eventId.Trim());
                var linked = new List<(string Rid, DateTimeOffset At)>();
                await using var reader = await rSel.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    if (DateTimeOffset.TryParse(reader.GetString(1), out var at))
                        linked.Add((reader.GetString(0), at + delta));
                }
                foreach (var (rid, nextAt) in linked)
                    await RescheduleReminderAsync(rid, nextAt, ct);
            }
        }
        return ok;
    }

    public async Task<bool> DeleteCalendarEventAsync(string eventId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using (var delLinked = conn.CreateCommand())
        {
            delLinked.CommandText = "DELETE FROM Reminders WHERE LinkedEventId = $id;";
            delLinked.Parameters.AddWithValue("$id", eventId.Trim());
            await delLinked.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM CalendarEvents WHERE EventId = $id;";
        cmd.Parameters.AddWithValue("$id", eventId.Trim());
        var deleted = await cmd.ExecuteNonQueryAsync(ct) > 0;
        if (deleted)
            await DeleteAnnotationsForTargetAsync(conn, "calendar", eventId.Trim(), ct);
        return deleted;
    }

    public async Task SetLinkedReminderLeadAsync(CalendarEventRecord ev, int remindMinutesBefore, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        if (remindMinutesBefore <= 0)
        {
            await using var del = conn.CreateCommand();
            del.CommandText = "DELETE FROM Reminders WHERE LinkedEventId = $id AND Status = 'Pending';";
            del.Parameters.AddWithValue("$id", ev.EventId);
            await del.ExecuteNonQueryAsync(ct);
            return;
        }

        var nextRemindAt = ev.StartAt.AddMinutes(-remindMinutesBefore);
        await using var upd = conn.CreateCommand();
        upd.CommandText = "UPDATE Reminders SET RemindAt = $at WHERE LinkedEventId = $id AND Status = 'Pending';";
        upd.Parameters.AddWithValue("$at", nextRemindAt.ToString("O"));
        upd.Parameters.AddWithValue("$id", ev.EventId);
        var updated = await upd.ExecuteNonQueryAsync(ct);

        if (updated == 0 && !ev.AllDay)
        {
            var isRec = AiSecretary.Desktop.Services.RecurrenceHelper.IsRecurring(ev.Recurrence);
            var normRule = isRec ? AiSecretary.Desktop.Services.RecurrenceHelper.Normalize(ev.Recurrence) : null;
            if (nextRemindAt > DateTimeOffset.UtcNow || isRec)
            {
                var rec = new ReminderRecord(
                    ReminderId: Guid.NewGuid().ToString("N"),
                    ConversationId: ev.ConversationId,
                    Title: $"{ev.Title}（會前提醒）",
                    Message: $"{ev.StartAt.ToLocalTime():HH:mm} 開始：{ev.Title}",
                    RelatedMailId: null,
                    RemindAt: nextRemindAt,
                    Status: "Pending",
                    IntervalMinutes: 0,
                    SkillName: null,
                    SkillArgsJson: null,
                    WorkdaysOnly: AiSecretary.Desktop.Services.RecurrenceHelper.IsWorkdaysRule(normRule),
                    ExceptionDatesJson: ev.ExceptionDatesJson,
                    LinkedEventId: ev.EventId,
                    RecurrenceRule: normRule);
                await CreateReminderAsync(rec, ct);
            }
        }
    }

    public async Task SetLinkedReminderLeadForDatesAsync(
        CalendarEventRecord ev,
        int remindMinutesBefore,
        IReadOnlyList<string> datesYmd,
        CancellationToken ct = default)
    {
        if (datesYmd.Count == 0)
        {
            await SetLinkedReminderLeadAsync(ev, remindMinutesBefore, ct);
            return;
        }

        await using var conn = await OpenAsync(ct);
        var targetDates = datesYmd.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (targetDates.Count == 0) return;

        // 1. Add target dates to ExceptionDatesJson of any existing recurring linked reminders
        await using (var rSel = conn.CreateCommand())
        {
            rSel.CommandText = "SELECT ReminderId, ExceptionDatesJson FROM Reminders WHERE LinkedEventId = $id AND Status = 'Pending' AND (IntervalMinutes > 0 OR (RecurrenceRule IS NOT NULL AND lower(RecurrenceRule) != 'none'));";
            rSel.Parameters.AddWithValue("$id", ev.EventId);
            var recurring = new List<(string Rid, string Ex)>();
            await using var reader = await rSel.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                recurring.Add((reader.GetString(0), reader.IsDBNull(1) ? "[]" : reader.GetString(1)));

            foreach (var (rid, rex) in recurring)
            {
                var rset = ParseExceptionDates(rex);
                foreach (var ymd in targetDates) rset.Add(ymd);
                await using var rUpd = conn.CreateCommand();
                rUpd.CommandText = "UPDATE Reminders SET ExceptionDatesJson = $ex WHERE ReminderId = $rid;";
                rUpd.Parameters.AddWithValue("$ex", JsonSerializer.Serialize(rset.OrderBy(x => x).ToList()));
                rUpd.Parameters.AddWithValue("$rid", rid);
                await rUpd.ExecuteNonQueryAsync(ct);
            }
        }

        // 2. Remove any prior one-off override reminders on those same dates and create new ones if remindMinutesBefore > 0
        var localStart = ev.StartAt.ToLocalTime();
        foreach (var ymd in targetDates)
        {
            if (!DateTime.TryParseExact(ymd, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var datePart))
                continue;

            var occStart = new DateTimeOffset(
                datePart.Year, datePart.Month, datePart.Day,
                localStart.Hour, localStart.Minute, localStart.Second,
                localStart.Offset);

            // Delete existing one-off reminder for this event on that local date
            await using (var delSel = conn.CreateCommand())
            {
                delSel.CommandText = "SELECT ReminderId, RemindAt FROM Reminders WHERE LinkedEventId = $id AND Status = 'Pending' AND IntervalMinutes = 0 AND (RecurrenceRule IS NULL OR lower(RecurrenceRule) = 'none');";
                delSel.Parameters.AddWithValue("$id", ev.EventId);
                var toDelete = new List<string>();
                await using var rdr = await delSel.ExecuteReaderAsync(ct);
                while (await rdr.ReadAsync(ct))
                {
                    if (DateTimeOffset.TryParse(rdr.GetString(1), out var ra) && ra.ToLocalTime().Date == datePart.Date)
                        toDelete.Add(rdr.GetString(0));
                }
                foreach (var rid in toDelete)
                    await DeleteReminderAsync(rid, ct);
            }

            if (remindMinutesBefore > 0)
            {
                var nextRemindAt = occStart.AddMinutes(-remindMinutesBefore);
                if (nextRemindAt > DateTimeOffset.UtcNow)
                {
                    var rec = new ReminderRecord(
                        ReminderId: Guid.NewGuid().ToString("N"),
                        ConversationId: ev.ConversationId,
                        Title: $"{ev.Title}（會前提醒）",
                        Message: $"{occStart:HH:mm} 開始：{ev.Title}",
                        RelatedMailId: null,
                        RemindAt: nextRemindAt,
                        Status: "Pending",
                        IntervalMinutes: 0,
                        SkillName: null,
                        SkillArgsJson: null,
                        WorkdaysOnly: false,
                        ExceptionDatesJson: "[]",
                        LinkedEventId: ev.EventId);
                    await CreateReminderAsync(rec, ct);
                }
            }
        }
    }

    public async Task<int> UpdateAllLinkedRemindersLeadAsync(string conversationId, int remindMinutesBefore, CancellationToken ct = default)
    {
        var events = await GetCalendarEventsAsync(conversationId, null, null, ct);
        var now = DateTimeOffset.UtcNow;
        int count = 0;
        foreach (var ev in events.Where(e => !e.AllDay && !e.Category.Equals("leave", StringComparison.OrdinalIgnoreCase) && !e.Category.Equals("holiday", StringComparison.OrdinalIgnoreCase)))
        {
            if (ev.Recurrence == "none" && ev.StartAt <= now) continue;
            await SetLinkedReminderLeadAsync(ev, remindMinutesBefore, ct);
            count++;
        }
        return count;
    }

    // ─── StandingWatchers ─────────────────────────────────────────────

    public async Task CreateWatcherAsync(StandingWatcherRecord w, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO StandingWatchers
                (WatcherId, ConversationId, Title, Kind, Url, SelectorOrPath, ConditionType, ThresholdValue,
                 IntervalSeconds, LastSnapshot, LastCheckedAt, NextCheckAt, FailureCount, Status, CreatedAt)
            VALUES
                (@id, @conv, @title, @kind, @url, @sel, @cond, @thresh, @interval, @snap, @lastAt, @nextAt, @fail, @status, @created);
            """;
        cmd.Parameters.AddWithValue("@id", w.WatcherId);
        cmd.Parameters.AddWithValue("@conv", w.ConversationId);
        cmd.Parameters.AddWithValue("@title", w.Title);
        cmd.Parameters.AddWithValue("@kind", w.Kind);
        cmd.Parameters.AddWithValue("@url", w.Url);
        cmd.Parameters.AddWithValue("@sel", w.SelectorOrPath);
        cmd.Parameters.AddWithValue("@cond", w.ConditionType);
        cmd.Parameters.AddWithValue("@thresh", (object?)w.ThresholdValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@interval", w.IntervalSeconds);
        cmd.Parameters.AddWithValue("@snap", (object?)w.LastSnapshot ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastAt", w.LastCheckedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@nextAt", w.NextCheckAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@fail", w.FailureCount);
        cmd.Parameters.AddWithValue("@status", w.Status);
        cmd.Parameters.AddWithValue("@created", w.CreatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<StandingWatcherRecord>> GetWatchersAsync(string? conversationId = null, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = string.IsNullOrWhiteSpace(conversationId)
            ? "SELECT WatcherId, ConversationId, Title, Kind, Url, SelectorOrPath, ConditionType, ThresholdValue, IntervalSeconds, LastSnapshot, LastCheckedAt, NextCheckAt, FailureCount, Status, CreatedAt FROM StandingWatchers ORDER BY CreatedAt DESC;"
            : "SELECT WatcherId, ConversationId, Title, Kind, Url, SelectorOrPath, ConditionType, ThresholdValue, IntervalSeconds, LastSnapshot, LastCheckedAt, NextCheckAt, FailureCount, Status, CreatedAt FROM StandingWatchers WHERE ConversationId = @conv ORDER BY CreatedAt DESC;";
        if (!string.IsNullOrWhiteSpace(conversationId))
            cmd.Parameters.AddWithValue("@conv", conversationId);

        var list = new List<StandingWatcherRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new StandingWatcherRecord(
                WatcherId: r.GetString(0),
                ConversationId: r.GetString(1),
                Title: r.GetString(2),
                Kind: r.GetString(3),
                Url: r.GetString(4),
                SelectorOrPath: r.GetString(5),
                ConditionType: r.GetString(6),
                ThresholdValue: r.IsDBNull(7) ? null : r.GetString(7),
                IntervalSeconds: r.GetInt32(8),
                LastSnapshot: r.IsDBNull(9) ? null : r.GetString(9),
                LastCheckedAt: r.IsDBNull(10) ? null : DateTimeOffset.Parse(r.GetString(10)),
                NextCheckAt: r.IsDBNull(11) ? null : DateTimeOffset.Parse(r.GetString(11)),
                FailureCount: r.GetInt32(12),
                Status: r.GetString(13),
                CreatedAt: DateTimeOffset.Parse(r.GetString(14))));
        }
        return list;
    }

    public async Task UpdateWatcherStateAsync(
        string watcherId,
        string? lastSnapshot,
        DateTimeOffset? lastCheckedAt,
        DateTimeOffset? nextCheckAt,
        int failureCount,
        string status,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE StandingWatchers
            SET LastSnapshot = @snap,
                LastCheckedAt = @lastAt,
                NextCheckAt = @nextAt,
                FailureCount = @fail,
                Status = @status
            WHERE WatcherId = @id;
            """;
        cmd.Parameters.AddWithValue("@id", watcherId);
        cmd.Parameters.AddWithValue("@snap", (object?)lastSnapshot ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lastAt", lastCheckedAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@nextAt", nextCheckAt?.ToString("O") ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@fail", failureCount);
        cmd.Parameters.AddWithValue("@status", status);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteWatcherAsync(string watcherId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM StandingWatchers WHERE WatcherId = @id;";
        cmd.Parameters.AddWithValue("@id", watcherId);
        var deleted = await cmd.ExecuteNonQueryAsync(ct) > 0;
        if (deleted)
            await DeleteAnnotationsForTargetAsync(conn, "watcher", watcherId, ct);
        return deleted;
    }

    // ─── WorkflowCaches & BranchTelemetry ─────────────────────────────

    public async Task<List<WorkflowCacheRecord>> GetWorkflowCachesAsync(string conversationId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT WorkflowId, ConversationId, GoalSignature, StepsJson, PredictorCounter, HitCount, MissCount, Status, LastUsedAt, CreatedAt
            FROM WorkflowCaches
            WHERE ConversationId = @conv
            ORDER BY PredictorCounter DESC, LastUsedAt DESC;
            """;
        cmd.Parameters.AddWithValue("@conv", conversationId);

        var list = new List<WorkflowCacheRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new WorkflowCacheRecord(
                WorkflowId: r.GetString(0),
                ConversationId: r.GetString(1),
                GoalSignature: r.GetString(2),
                StepsJson: r.GetString(3),
                PredictorCounter: r.GetInt32(4),
                HitCount: r.GetInt32(5),
                MissCount: r.GetInt32(6),
                Status: r.GetString(7),
                LastUsedAt: DateTimeOffset.Parse(r.GetString(8)),
                CreatedAt: DateTimeOffset.Parse(r.GetString(9))));
        }
        return list;
    }

    public async Task UpsertWorkflowCacheAsync(WorkflowCacheRecord rec, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO WorkflowCaches
                (WorkflowId, ConversationId, GoalSignature, StepsJson, PredictorCounter, HitCount, MissCount, Status, LastUsedAt, CreatedAt)
            VALUES
                (@id, @conv, @sig, @steps, @cnt, @hits, @miss, @status, @last, @created)
            ON CONFLICT(WorkflowId) DO UPDATE SET
                StepsJson = excluded.StepsJson,
                PredictorCounter = excluded.PredictorCounter,
                HitCount = excluded.HitCount,
                MissCount = excluded.MissCount,
                Status = excluded.Status,
                LastUsedAt = excluded.LastUsedAt;
            """;
        cmd.Parameters.AddWithValue("@id", rec.WorkflowId);
        cmd.Parameters.AddWithValue("@conv", rec.ConversationId);
        cmd.Parameters.AddWithValue("@sig", rec.GoalSignature);
        cmd.Parameters.AddWithValue("@steps", rec.StepsJson);
        cmd.Parameters.AddWithValue("@cnt", rec.PredictorCounter);
        cmd.Parameters.AddWithValue("@hits", rec.HitCount);
        cmd.Parameters.AddWithValue("@miss", rec.MissCount);
        cmd.Parameters.AddWithValue("@status", rec.Status);
        cmd.Parameters.AddWithValue("@last", rec.LastUsedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@created", rec.CreatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordBranchOutcomeAsync(bool hit, long estimatedTokensSaved, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE BranchTelemetry
            SET Predictions = Predictions + 1,
                Hits = Hits + @hit,
                Mispredictions = Mispredictions + @miss,
                EstimatedTokensSaved = EstimatedTokensSaved + @saved
            WHERE Id = 1;
            """;
        cmd.Parameters.AddWithValue("@hit", hit ? 1 : 0);
        cmd.Parameters.AddWithValue("@miss", hit ? 0 : 1);
        cmd.Parameters.AddWithValue("@saved", hit ? Math.Max(0, estimatedTokensSaved) : 0);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<BranchTelemetryStats> GetBranchTelemetryAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM WorkflowCaches),
                (SELECT COUNT(*) FROM WorkflowCaches WHERE Status = 'Hot'),
                (SELECT COUNT(*) FROM WorkflowCaches WHERE Status = 'Cold'),
                Predictions, Hits, Mispredictions, EstimatedTokensSaved
            FROM BranchTelemetry WHERE Id = 1;
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
        {
            return new BranchTelemetryStats(
                TotalWorkflows: r.GetInt32(0),
                HotWorkflows: r.GetInt32(1),
                ColdWorkflows: r.GetInt32(2),
                Predictions: r.GetInt64(3),
                Hits: r.GetInt64(4),
                Mispredictions: r.GetInt64(5),
                EstimatedTokensSaved: r.GetInt64(6));
        }
        return new BranchTelemetryStats(0, 0, 0, 0, 0, 0, 0);
    }

    // ─── UiLocaleCache ────────────────────────────────────────────────

    public async Task<Dictionary<string, string>> GetUiLocaleCacheAsync(string language, CancellationToken ct = default)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MsgKey, Template FROM UiLocaleCache WHERE Language = @lang;";
        cmd.Parameters.AddWithValue("@lang", language);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            map[r.GetString(0)] = r.GetString(1);
        return map;
    }

    public async Task UpsertUiLocaleCacheAsync(string language, string msgKey, string template, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO UiLocaleCache (Language, MsgKey, Template, UpdatedAt)
            VALUES (@lang, @key, @tpl, @at)
            ON CONFLICT(Language, MsgKey) DO UPDATE SET
                Template = excluded.Template,
                UpdatedAt = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("@lang", language);
        cmd.Parameters.AddWithValue("@key", msgKey);
        cmd.Parameters.AddWithValue("@tpl", template);
        cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ─── AutoStateRules (zero-hook 2-bit autonomous state machine) ───

    public async Task<AutoStateRuleRecord?> GetAutoStateRuleAsync(string triggerKey, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TriggerKey, StateName, StepsJson, PredictorCounter, HitCount, MissCount, Status, LastSeenAt, CooldownUntil, ProposalText FROM AutoStateRules WHERE TriggerKey = @k LIMIT 1;";
        cmd.Parameters.AddWithValue("@k", triggerKey);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new AutoStateRuleRecord(
            TriggerKey: r.GetString(0),
            StateName: r.GetString(1),
            StepsJson: r.GetString(2),
            PredictorCounter: r.GetInt32(3),
            HitCount: r.GetInt32(4),
            MissCount: r.GetInt32(5),
            Status: r.GetString(6),
            LastSeenAt: DateTimeOffset.Parse(r.GetString(7)),
            CooldownUntil: r.IsDBNull(8) || string.IsNullOrWhiteSpace(r.GetString(8)) ? null : DateTimeOffset.Parse(r.GetString(8)),
            ProposalText: r.IsDBNull(9) ? null : r.GetString(9));
    }

    public async Task<List<AutoStateRuleRecord>> GetAllAutoStateRulesAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT TriggerKey, StateName, StepsJson, PredictorCounter, HitCount, MissCount, Status, LastSeenAt, CooldownUntil, ProposalText FROM AutoStateRules ORDER BY LastSeenAt DESC LIMIT 100;";
        var list = new List<AutoStateRuleRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new AutoStateRuleRecord(
                TriggerKey: r.GetString(0),
                StateName: r.GetString(1),
                StepsJson: r.GetString(2),
                PredictorCounter: r.GetInt32(3),
                HitCount: r.GetInt32(4),
                MissCount: r.GetInt32(5),
                Status: r.GetString(6),
                LastSeenAt: DateTimeOffset.Parse(r.GetString(7)),
                CooldownUntil: r.IsDBNull(8) || string.IsNullOrWhiteSpace(r.GetString(8)) ? null : DateTimeOffset.Parse(r.GetString(8)),
                ProposalText: r.IsDBNull(9) ? null : r.GetString(9)));
        }
        return list;
    }

    public async Task UpsertAutoStateRuleAsync(AutoStateRuleRecord rec, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AutoStateRules (TriggerKey, StateName, StepsJson, PredictorCounter, HitCount, MissCount, Status, LastSeenAt, CooldownUntil, ProposalText)
            VALUES (@k, @state, @steps, @cnt, @hits, @miss, @status, @seen, @cd, @prop)
            ON CONFLICT(TriggerKey) DO UPDATE SET
                StateName = excluded.StateName,
                StepsJson = excluded.StepsJson,
                PredictorCounter = excluded.PredictorCounter,
                HitCount = excluded.HitCount,
                MissCount = excluded.MissCount,
                Status = excluded.Status,
                LastSeenAt = excluded.LastSeenAt,
                CooldownUntil = excluded.CooldownUntil,
                ProposalText = excluded.ProposalText;
            """;
        cmd.Parameters.AddWithValue("@k", rec.TriggerKey);
        cmd.Parameters.AddWithValue("@state", rec.StateName);
        cmd.Parameters.AddWithValue("@steps", rec.StepsJson);
        cmd.Parameters.AddWithValue("@cnt", rec.PredictorCounter);
        cmd.Parameters.AddWithValue("@hits", rec.HitCount);
        cmd.Parameters.AddWithValue("@miss", rec.MissCount);
        cmd.Parameters.AddWithValue("@status", rec.Status);
        cmd.Parameters.AddWithValue("@seen", rec.LastSeenAt.ToString("O"));
        cmd.Parameters.AddWithValue("@cd", rec.CooldownUntil.HasValue ? rec.CooldownUntil.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("@prop", string.IsNullOrWhiteSpace(rec.ProposalText) ? DBNull.Value : rec.ProposalText);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> DeleteAutoStateRuleAsync(string triggerKey, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM AutoStateRules WHERE TriggerKey = @k;";
        cmd.Parameters.AddWithValue("@k", triggerKey);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    // ─── Observability: LlmCallLogs, LearningEvents, PredictionLogs ───

    public async Task InsertLlmCallLogAsync(LlmCallLogRecord rec, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO LlmCallLogs (TimestampUtc, Agent, Tier, Model, CachePolicy, Priority, UncachedInput, CacheWrite, CacheRead, Output, LatencyMs, Succeeded, Error, StopReason, RawTextLength)
            VALUES (@ts, @agent, @tier, @model, @cp, @prio, @inTok, @cw, @cr, @outTok, @lat, @ok, @err, @stop, @rawLen);
            """;
        cmd.Parameters.AddWithValue("@ts", rec.TimestampUtc.ToString("O"));
        cmd.Parameters.AddWithValue("@agent", rec.Agent);
        cmd.Parameters.AddWithValue("@tier", rec.Tier);
        cmd.Parameters.AddWithValue("@model", rec.Model);
        cmd.Parameters.AddWithValue("@cp", rec.CachePolicy);
        cmd.Parameters.AddWithValue("@prio", rec.Priority);
        cmd.Parameters.AddWithValue("@inTok", rec.UncachedInput);
        cmd.Parameters.AddWithValue("@cw", rec.CacheWrite);
        cmd.Parameters.AddWithValue("@cr", rec.CacheRead);
        cmd.Parameters.AddWithValue("@outTok", rec.Output);
        cmd.Parameters.AddWithValue("@lat", rec.LatencyMs);
        cmd.Parameters.AddWithValue("@ok", rec.Succeeded ? 1 : 0);
        cmd.Parameters.AddWithValue("@err", string.IsNullOrWhiteSpace(rec.Error) ? DBNull.Value : rec.Error);
        cmd.Parameters.AddWithValue("@stop", string.IsNullOrWhiteSpace(rec.StopReason) ? DBNull.Value : rec.StopReason);
        cmd.Parameters.AddWithValue("@rawLen", rec.RawTextLength);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<LlmCallLogRecord>> GetRecentLlmCallLogsAsync(int limit = 40, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, TimestampUtc, Agent, Tier, Model, CachePolicy, Priority, UncachedInput, CacheWrite, CacheRead, Output, LatencyMs, Succeeded, Error, StopReason, RawTextLength FROM LlmCallLogs ORDER BY Id DESC LIMIT @lim;";
        cmd.Parameters.AddWithValue("@lim", Math.Clamp(limit, 1, 200));
        var list = new List<LlmCallLogRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new LlmCallLogRecord(
                Id: r.GetInt64(0),
                TimestampUtc: DateTimeOffset.Parse(r.GetString(1)),
                Agent: r.GetString(2),
                Tier: r.GetString(3),
                Model: r.GetString(4),
                CachePolicy: r.GetString(5),
                Priority: r.GetString(6),
                UncachedInput: r.GetInt32(7),
                CacheWrite: r.GetInt32(8),
                CacheRead: r.GetInt32(9),
                Output: r.GetInt32(10),
                LatencyMs: r.GetInt32(11),
                Succeeded: r.GetInt32(12) != 0,
                Error: r.IsDBNull(13) ? null : r.GetString(13),
                StopReason: r.IsDBNull(14) ? null : r.GetString(14),
                RawTextLength: r.IsDBNull(15) ? 0 : r.GetInt32(15)));
        }
        return list;
    }

    public async Task<List<AgentUsageSummary>> GetLlmUsageByAgentAsync(CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Agent, COUNT(*), SUM(UncachedInput), SUM(CacheWrite), SUM(CacheRead), SUM(Output), AVG(LatencyMs)
            FROM LlmCallLogs
            GROUP BY Agent
            ORDER BY (SUM(UncachedInput) + SUM(CacheWrite) + SUM(CacheRead) + SUM(Output)) DESC;
            """;
        var list = new List<AgentUsageSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new AgentUsageSummary(
                Agent: r.GetString(0),
                Calls: r.GetInt32(1),
                UncachedInput: r.IsDBNull(2) ? 0 : r.GetInt64(2),
                CacheWrite: r.IsDBNull(3) ? 0 : r.GetInt64(3),
                CacheRead: r.IsDBNull(4) ? 0 : r.GetInt64(4),
                Output: r.IsDBNull(5) ? 0 : r.GetInt64(5),
                AvgLatencyMs: r.IsDBNull(6) ? 0 : r.GetDouble(6)));
        }
        return list;
    }

    public async Task InsertLearningEventAsync(
        string learner,
        string subjectKey,
        string eventType,
        string? beforeState = null,
        string? afterState = null,
        string? evidence = null,
        CancellationToken ct = default)
    {
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO LearningEvents (TimestampUtc, Learner, SubjectKey, EventType, BeforeState, AfterState, Evidence)
                VALUES (@ts, @learner, @subj, @evt, @bef, @aft, @ev);
                """;
            cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@learner", learner);
            cmd.Parameters.AddWithValue("@subj", subjectKey);
            cmd.Parameters.AddWithValue("@evt", eventType);
            cmd.Parameters.AddWithValue("@bef", string.IsNullOrWhiteSpace(beforeState) ? DBNull.Value : beforeState);
            cmd.Parameters.AddWithValue("@aft", string.IsNullOrWhiteSpace(afterState) ? DBNull.Value : afterState);
            cmd.Parameters.AddWithValue("@ev", string.IsNullOrWhiteSpace(evidence) ? DBNull.Value : evidence);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch { }
    }

    public async Task<List<LearningEventRecord>> GetRecentLearningEventsAsync(int limit = 50, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, TimestampUtc, Learner, SubjectKey, EventType, BeforeState, AfterState, Evidence FROM LearningEvents ORDER BY Id DESC LIMIT @lim;";
        cmd.Parameters.AddWithValue("@lim", Math.Clamp(limit, 1, 200));
        var list = new List<LearningEventRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new LearningEventRecord(
                Id: r.GetInt64(0),
                TimestampUtc: DateTimeOffset.Parse(r.GetString(1)),
                Learner: r.GetString(2),
                SubjectKey: r.GetString(3),
                EventType: r.GetString(4),
                BeforeState: r.IsDBNull(5) ? null : r.GetString(5),
                AfterState: r.IsDBNull(6) ? null : r.GetString(6),
                Evidence: r.IsDBNull(7) ? null : r.GetString(7)));
        }
        return list;
    }

    public Task InsertPredictionLogAsync(
        string source,
        string subjectKey,
        string queryText,
        string predictedAction,
        bool execSucceeded,
        long tokensSaved,
        CancellationToken ct = default)
        => InsertPredictionLogReturningIdAsync(source, subjectKey, queryText, predictedAction, execSucceeded, tokensSaved, ct);

    public async Task<long> InsertPredictionLogReturningIdAsync(
        string source,
        string subjectKey,
        string queryText,
        string predictedAction,
        bool execSucceeded,
        long tokensSaved,
        CancellationToken ct = default)
    {
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO PredictionLogs (TimestampUtc, Source, SubjectKey, QueryText, PredictedAction, ExecSucceeded, ImplicitFeedback, TokensSaved)
                VALUES (@ts, @src, @subj, @q, @act, @ok, NULL, @saved);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@src", source);
            cmd.Parameters.AddWithValue("@subj", subjectKey);
            cmd.Parameters.AddWithValue("@q", queryText.Length > 200 ? queryText[..200] : queryText);
            cmd.Parameters.AddWithValue("@act", predictedAction);
            cmd.Parameters.AddWithValue("@ok", execSucceeded ? 1 : 0);
            cmd.Parameters.AddWithValue("@saved", tokensSaved);
            var scalar = await cmd.ExecuteScalarAsync(ct);
            return scalar is not null && scalar is not DBNull ? Convert.ToInt64(scalar) : 0L;
        }
        catch
        {
            return 0L;
        }
    }

    public async Task UpdatePredictionLogFeedbackAsync(long logId, string feedback, CancellationToken ct = default)
    {
        if (logId <= 0 || string.IsNullOrWhiteSpace(feedback)) return;
        try
        {
            await using var conn = await OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE PredictionLogs SET ImplicitFeedback = @fb WHERE Id = @id;";
            cmd.Parameters.AddWithValue("@fb", feedback);
            cmd.Parameters.AddWithValue("@id", logId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch { }
    }

    public async Task<List<PredictionLogRecord>> GetRecentPredictionLogsAsync(int limit = 40, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, TimestampUtc, Source, SubjectKey, QueryText, PredictedAction, ExecSucceeded, ImplicitFeedback, TokensSaved FROM PredictionLogs ORDER BY Id DESC LIMIT @lim;";
        cmd.Parameters.AddWithValue("@lim", Math.Clamp(limit, 1, 200));
        var list = new List<PredictionLogRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new PredictionLogRecord(
                Id: r.GetInt64(0),
                TimestampUtc: DateTimeOffset.Parse(r.GetString(1)),
                Source: r.GetString(2),
                SubjectKey: r.GetString(3),
                QueryText: r.GetString(4),
                PredictedAction: r.GetString(5),
                ExecSucceeded: r.GetInt32(6) != 0,
                ImplicitFeedback: r.IsDBNull(7) ? null : r.GetString(7),
                TokensSaved: r.GetInt64(8)));
        }
        return list;
    }

    public async Task PruneOldObservabilityLogsAsync(int retentionDays = 30, CancellationToken ct = default)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow.AddDays(-Math.Clamp(retentionDays, 1, 365)).ToString("O");
            await using var conn = await OpenAsync(ct);
            foreach (var table in new[] { "LlmCallLogs", "LearningEvents", "PredictionLogs" })
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {table} WHERE TimestampUtc < @cutoff;";
                cmd.Parameters.AddWithValue("@cutoff", cutoff);
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        catch { }
    }

    public async Task<AutonomyPolicyRecord?> GetAutonomyPolicyAsync(string actionClass, string domain, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ActionClass, Domain, Level, Accepted, Rejected, UpdatedAt
            FROM AutonomyPolicies
            WHERE ActionClass = @ac AND Domain = @dom;
            """;
        cmd.Parameters.AddWithValue("@ac", actionClass);
        cmd.Parameters.AddWithValue("@dom", domain);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new AutonomyPolicyRecord(
            ActionClass: r.GetString(0),
            Domain: r.GetString(1),
            Level: r.GetInt32(2),
            Accepted: r.GetInt32(3),
            Rejected: r.GetInt32(4),
            UpdatedAt: DateTimeOffset.Parse(r.GetString(5)));
    }

    public async Task UpsertAutonomyPolicyAsync(AutonomyPolicyRecord policy, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO AutonomyPolicies (ActionClass, Domain, Level, Accepted, Rejected, UpdatedAt)
            VALUES (@ac, @dom, @lvl, @acc, @rej, @ts)
            ON CONFLICT(ActionClass, Domain) DO UPDATE SET
                Level = excluded.Level,
                Accepted = excluded.Accepted,
                Rejected = excluded.Rejected,
                UpdatedAt = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("@ac", policy.ActionClass);
        cmd.Parameters.AddWithValue("@dom", policy.Domain);
        cmd.Parameters.AddWithValue("@lvl", policy.Level);
        cmd.Parameters.AddWithValue("@acc", policy.Accepted);
        cmd.Parameters.AddWithValue("@rej", policy.Rejected);
        cmd.Parameters.AddWithValue("@ts", policy.UpdatedAt.ToUniversalTime().ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<AutonomyPolicyRecord>> GetAllAutonomyPoliciesAsync(CancellationToken ct = default)
    {
        var list = new List<AutonomyPolicyRecord>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ActionClass, Domain, Level, Accepted, Rejected, UpdatedAt
            FROM AutonomyPolicies
            ORDER BY UpdatedAt DESC;
            """;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new AutonomyPolicyRecord(
                ActionClass: r.GetString(0),
                Domain: r.GetString(1),
                Level: r.GetInt32(2),
                Accepted: r.GetInt32(3),
                Rejected: r.GetInt32(4),
                UpdatedAt: DateTimeOffset.Parse(r.GetString(5))));
        }
        return list;
    }

    public async Task<bool> DeleteAutonomyPolicyAsync(string actionClass, string domain, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM AutonomyPolicies WHERE ActionClass = @ac AND Domain = @dom;";
        cmd.Parameters.AddWithValue("@ac", actionClass);
        cmd.Parameters.AddWithValue("@dom", domain);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteWorkflowCacheAsync(string workflowId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM WorkflowCaches WHERE WorkflowId = @id;";
        cmd.Parameters.AddWithValue("@id", workflowId);
        var deleted = await cmd.ExecuteNonQueryAsync(ct) > 0;
        if (deleted)
            await DeleteAnnotationsForTargetAsync(conn, "workflow", workflowId, ct);
        return deleted;
    }

    public async Task<bool> DeletePageIntentAsync(string patternKey, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM PageIntents WHERE PatternKey = @k;";
        cmd.Parameters.AddWithValue("@k", patternKey);
        var deleted = await cmd.ExecuteNonQueryAsync(ct) > 0;
        if (deleted)
            await DeleteAnnotationsForTargetAsync(conn, "page_intent", patternKey, ct);
        return deleted;
    }

    // ─── MemoryAnnotations (Cross-memory tags with cascade delete) ──

    public async Task UpsertMemoryAnnotationAsync(MemoryAnnotationRecord rec, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MemoryAnnotations (AnnotationId, ConversationId, TargetType, TargetId, Note, SourceFactId, CreatedAt, UpdatedAt)
            VALUES (@id, @conv, @tt, @tid, @note, @sf, @ca, @ua)
            ON CONFLICT(AnnotationId) DO UPDATE SET
                Note = excluded.Note,
                SourceFactId = COALESCE(excluded.SourceFactId, MemoryAnnotations.SourceFactId),
                UpdatedAt = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("@id", rec.AnnotationId);
        cmd.Parameters.AddWithValue("@conv", rec.ConversationId);
        cmd.Parameters.AddWithValue("@tt", rec.TargetType);
        cmd.Parameters.AddWithValue("@tid", rec.TargetId);
        cmd.Parameters.AddWithValue("@note", rec.Note);
        cmd.Parameters.AddWithValue("@sf", (object?)rec.SourceFactId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ca", rec.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@ua", rec.UpdatedAt.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<MemoryAnnotationRecord>> GetMemoryAnnotationsAsync(
        string? targetType = null,
        string? targetId = null,
        string? sourceFactId = null,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(targetType))
        {
            where.Add("TargetType = @tt");
            cmd.Parameters.AddWithValue("@tt", targetType.Trim().ToLowerInvariant());
        }
        if (!string.IsNullOrWhiteSpace(targetId))
        {
            where.Add("TargetId = @tid");
            cmd.Parameters.AddWithValue("@tid", targetId.Trim());
        }
        if (!string.IsNullOrWhiteSpace(sourceFactId))
        {
            where.Add("SourceFactId = @sf");
            cmd.Parameters.AddWithValue("@sf", sourceFactId.Trim());
        }
        var sqlWhere = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";
        cmd.CommandText = $"""
            SELECT AnnotationId, ConversationId, TargetType, TargetId, Note, SourceFactId, CreatedAt, UpdatedAt
            FROM MemoryAnnotations
            {sqlWhere}
            ORDER BY UpdatedAt DESC
            LIMIT 50;
            """;

        var list = new List<MemoryAnnotationRecord>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            list.Add(new MemoryAnnotationRecord(
                r.GetString(0),
                r.GetString(1),
                r.GetString(2),
                r.GetString(3),
                r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5),
                DateTimeOffset.Parse(r.GetString(6)),
                DateTimeOffset.Parse(r.GetString(7))));
        }
        return list;
    }

    public async Task<bool> UpdateMemoryAnnotationAsync(string annotationId, string note, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE MemoryAnnotations SET Note = @note, UpdatedAt = @ua WHERE AnnotationId = @id;";
        cmd.Parameters.AddWithValue("@id", annotationId.Trim());
        cmd.Parameters.AddWithValue("@note", note.Trim());
        cmd.Parameters.AddWithValue("@ua", DateTimeOffset.UtcNow.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> DeleteMemoryAnnotationAsync(string annotationId, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM MemoryAnnotations WHERE AnnotationId = @id;";
        cmd.Parameters.AddWithValue("@id", annotationId.Trim());
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static async Task DeleteAnnotationsForTargetAsync(
        Microsoft.Data.Sqlite.SqliteConnection conn,
        string targetType,
        string targetId,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM MemoryAnnotations WHERE TargetType = @tt AND TargetId = @tid;";
        cmd.Parameters.AddWithValue("@tt", targetType);
        cmd.Parameters.AddWithValue("@tid", targetId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
