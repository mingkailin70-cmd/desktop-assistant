using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using XiaoK.Core;

namespace XiaoK.Storage;

/// <summary>
/// Stores task category and lifecycle metadata through the Windows SQLite system library.
/// User prompts, model answers, notification bodies, attachment paths, and Result are never persisted.
/// </summary>
public sealed class SqliteTaskStore : ITaskStore
{
    private const int CurrentSchemaVersion = 5;
    private const int MaximumContactReplyStyles = 200;
    private const long MaximumLegacyJsonBytes = 10 * 1024 * 1024;
    private const string TasksJsonMigrationMarker = "tasks-json-v1";
    private const string ContactStylesMigrationMarker = "contact-styles-settings-v1";
    private static readonly JsonSerializerOptions LegacyJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly string? _legacyJsonPath;
    private readonly IReadOnlyList<ContactReplyStylePreference> _legacyContactReplyStyles;
    private readonly bool _legacyContactReplyStylesAvailable;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SqliteTaskStore(string path, string? legacyJsonPath = null,
        IEnumerable<ContactReplyStylePreference>? legacyContactReplyStyles = null,
        bool legacyContactReplyStylesAvailable = true)
    {
        _path = ValidateDatabasePath(path);
        _legacyJsonPath = string.IsNullOrWhiteSpace(legacyJsonPath) ? null : ValidateDatabasePath(legacyJsonPath);
        if (_legacyJsonPath is not null && string.Equals(_legacyJsonPath, _path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("旧 JSON 文件路径不能与 SQLite 数据库路径相同。", nameof(legacyJsonPath));
        _legacyContactReplyStyles = legacyContactReplyStyles?.ToArray() ?? [];
        _legacyContactReplyStylesAvailable = legacyContactReplyStylesAvailable;
        Initialize();
    }

    public async Task SaveAsync(TaskRecord task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        var sanitized = Sanitize(task);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => SaveCore(sanitized), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<TaskRecord>> GetRecentAsync(int count, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() => ReadRecentCore(Math.Clamp(count, 0, 100)), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ContactReplyStylePreference>> GetContactReplyStylesAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(ReadContactReplyStylesCore, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task ReplaceContactReplyStylesAsync(IEnumerable<ContactReplyStylePreference> preferences,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var sanitized = ValidateContactReplyStyles(preferences);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => ReplaceContactReplyStylesCore(sanitized), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task AppendApprovalAuditAsync(string actionId, string outcome, CancellationToken cancellationToken)
    {
        var record = CreateApprovalAuditRecord(actionId, outcome);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => AppendApprovalAuditCore(record), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ApprovalAuditRecord>> GetRecentApprovalAuditAsync(int count,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() => ReadRecentApprovalAuditCore(Math.Clamp(count, 0, 100)), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Creates a consistent SQLite backup without copying a live WAL file directly.</summary>
    public async Task<string> CreateBackupAsync(string backupPath, CancellationToken cancellationToken)
    {
        var destination = ValidateDatabasePath(backupPath);
        if (string.Equals(destination, _path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("数据库备份路径不能与活动数据库相同。", nameof(backupPath));

        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(() => BackupCore(destination, cancellationToken), cancellationToken);
            return destination;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Restores a validated v5 snapshot and retains the current database as a rollback copy.</summary>
    public async Task<string> RestoreBackupAsync(string backupPath, CancellationToken cancellationToken)
    {
        var source = ValidateDatabasePath(backupPath);
        if (string.Equals(source, _path, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("不能把活动数据库自身当作恢复备份。", nameof(backupPath));
        if (!File.Exists(source)) throw new FileNotFoundException("找不到所选 SQLite 备份。", source);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(() => RestoreBackupCore(source, cancellationToken), cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task<SqlitePersonalDataSummary> GetPersonalDataSummaryAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(ReadPersonalDataSummaryCore, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Clears persisted personal rows while retaining migration markers to prevent old-source reimport.</summary>
    public async Task<bool> ClearPersonalDataAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Task.Run(ClearPersonalDataCore, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private void Initialize()
    {
        var sqliteVersion = NativeSqlite.sqlite3_libversion_number();
        if (sqliteVersion < 3031000)
            throw new NotSupportedException($"Windows SQLite 版本过旧（{sqliteVersion}）；小K要求至少 3.31.0。数据库未打开或修改。");

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var hadExistingFile = File.Exists(_path) && new FileInfo(_path).Length > 0;
        using var database = SqliteDatabase.Open(_path, create: true);
        var version = database.ScalarInt32("PRAGMA user_version;");
        if (version > CurrentSchemaVersion)
            throw new InvalidDataException($"SQLite 数据库版本 {version} 高于当前程序支持的版本 {CurrentSchemaVersion}；没有修改数据库。");

        if (version < CurrentSchemaVersion && hadExistingFile)
            BackupCore(CreateMigrationBackupPath(), CancellationToken.None);

        database.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF;");

        if (version == 0)
        {
            database.Execute("BEGIN IMMEDIATE;");
            try
            {
                database.Execute("CREATE TABLE IF NOT EXISTS tasks ("
                    + "id TEXT PRIMARY KEY NOT NULL, kind TEXT NOT NULL, summary TEXT NOT NULL, status INTEGER NOT NULL, "
                    + "created_utc_ticks INTEGER NOT NULL, updated_utc_ticks INTEGER NOT NULL, error_code TEXT NULL);"
                    + "CREATE INDEX IF NOT EXISTS ix_tasks_updated ON tasks(updated_utc_ticks DESC);"
                    + "CREATE TABLE IF NOT EXISTS migration_state (name TEXT PRIMARY KEY NOT NULL, completed_utc_ticks INTEGER NOT NULL);"
                    + CreateContactReplyStylesTableSql()
                    + CreateApprovalAuditTableV5Sql()
                    + $"PRAGMA user_version={CurrentSchemaVersion};");
                database.Execute("COMMIT;");
            }
            catch
            {
                TryRollback(database);
                throw;
            }
        }
        else if (version == 1)
        {
            database.Execute("BEGIN IMMEDIATE;");
            try
            {
                database.Execute(CreateContactReplyStylesTableSql() + "PRAGMA user_version=2;");
                database.Execute("COMMIT;");
                version = 2;
            }
            catch
            {
                TryRollback(database);
                throw;
            }
        }

        if (version == 2)
        {
            database.Execute("BEGIN IMMEDIATE;");
            try
            {
                database.Execute(CreateApprovalAuditTableSql() + "PRAGMA user_version=3;");
                database.Execute("COMMIT;");
                version = 3;
            }
            catch
            {
                TryRollback(database);
                throw;
            }
        }

        if (version == 3)
        {
            database.Execute("BEGIN IMMEDIATE;");
            try
            {
                database.Execute("DROP INDEX IF EXISTS ix_approval_audit_created;"
                    + "ALTER TABLE approval_audit RENAME TO approval_audit_v3;"
                    + CreateApprovalAuditTableV4Sql()
                    + "INSERT INTO approval_audit(id,action_id,outcome,created_utc_ticks) "
                    + "SELECT id,action_id,outcome,created_utc_ticks FROM approval_audit_v3;"
                    + "DROP TABLE approval_audit_v3;"
                    + "PRAGMA user_version=4;");
                database.Execute("COMMIT;");
                version = 4;
            }
            catch
            {
                TryRollback(database);
                throw;
            }
        }

        if (version == 4)
        {
            database.Execute("BEGIN IMMEDIATE;");
            try
            {
                database.Execute("DROP INDEX IF EXISTS ix_approval_audit_created;"
                    + "ALTER TABLE approval_audit RENAME TO approval_audit_v4;"
                    + CreateApprovalAuditTableV5Sql()
                    + "INSERT INTO approval_audit(id,action_id,outcome,created_utc_ticks) "
                    + "SELECT id,action_id,outcome,created_utc_ticks FROM approval_audit_v4;"
                    + "DROP TABLE approval_audit_v4;"
                    + "PRAGMA user_version=5;");
                database.Execute("COMMIT;");
                version = 5;
            }
            catch
            {
                TryRollback(database);
                throw;
            }
        }

        MigrateLegacyJson(database);
        MigrateLegacyContactReplyStyles(database);
    }

    private static string CreateContactReplyStylesTableSql() => "CREATE TABLE IF NOT EXISTS contact_reply_styles ("
        + "contact_name_key TEXT PRIMARY KEY NOT NULL, contact_name TEXT NOT NULL, "
        + "style_id TEXT NOT NULL CHECK(style_id IN ('concise','formal','warm','casual','empathetic')), "
        + "source TEXT NOT NULL CHECK(source='user-confirmed'), updated_utc_ticks INTEGER NOT NULL);";

    private static string CreateApprovalAuditTableSql() => "CREATE TABLE IF NOT EXISTS approval_audit ("
        + "id TEXT PRIMARY KEY NOT NULL, action_id TEXT NOT NULL, outcome TEXT NOT NULL, created_utc_ticks INTEGER NOT NULL, "
        + "CHECK((action_id='message.send.v1' AND outcome IN ('confirmed','declined')) "
        + "OR (action_id='code.task.create.v1' AND outcome='run_dotnet_tests')));"
        + "CREATE INDEX IF NOT EXISTS ix_approval_audit_created ON approval_audit(created_utc_ticks DESC);";

    private static string CreateApprovalAuditTableV4Sql() => "CREATE TABLE IF NOT EXISTS approval_audit ("
        + "id TEXT PRIMARY KEY NOT NULL, action_id TEXT NOT NULL, outcome TEXT NOT NULL, created_utc_ticks INTEGER NOT NULL, "
        + "CHECK((action_id='message.send.v1' AND outcome IN ('confirmed','declined')) "
        + "OR (action_id='code.task.create.v1' AND outcome='run_dotnet_tests') "
        + "OR (action_id='code.patch.apply.v1' AND outcome='confirmed')));"
        + "CREATE INDEX IF NOT EXISTS ix_approval_audit_created ON approval_audit(created_utc_ticks DESC);";

    private static string CreateApprovalAuditTableV5Sql() => "CREATE TABLE IF NOT EXISTS approval_audit ("
        + "id TEXT PRIMARY KEY NOT NULL, action_id TEXT NOT NULL, outcome TEXT NOT NULL, created_utc_ticks INTEGER NOT NULL, "
        + "CHECK((action_id='message.send.v1' AND outcome IN ('confirmed','declined')) "
        + "OR (action_id='code.task.create.v1' AND outcome='run_dotnet_tests') "
        + "OR (action_id='code.patch.apply.v1' AND outcome='confirmed') "
        + "OR (action_id='file.delete.recycle-bin.v1' AND outcome IN ('confirmed','declined'))));"
        + "CREATE INDEX IF NOT EXISTS ix_approval_audit_created ON approval_audit(created_utc_ticks DESC);";

    private void MigrateLegacyJson(SqliteDatabase database)
    {
        if (_legacyJsonPath is null || !File.Exists(_legacyJsonPath)
            || database.ScalarText($"SELECT name FROM migration_state WHERE name='{TasksJsonMigrationMarker}' LIMIT 1;") is not null)
            return;

        List<TaskRecord> legacy;
        try
        {
            using var stream = new FileStream(_legacyJsonPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumLegacyJsonBytes)
                throw new InvalidDataException("旧任务文件超过 10 MiB 上限；没有修改旧文件或数据库。");
            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            legacy = JsonSerializer.Deserialize<List<TaskRecord>>(bytes, LegacyJsonOptions) ?? [];
        }
        catch (Exception ex) when (ex is not InvalidDataException
            && (ex is JsonException or IOException or UnauthorizedAccessException))
        {
            throw new InvalidDataException("旧任务文件无法安全读取；没有修改旧文件或数据库。", ex);
        }

        database.Execute("BEGIN IMMEDIATE;");
        try
        {
            foreach (var task in legacy)
            {
                if (task is null || !TrySanitize(task, out var sanitized)) continue;
                InsertOrReplace(database, sanitized);
            }
            using (var marker = database.Prepare("INSERT INTO migration_state(name,completed_utc_ticks) VALUES(?1,?2);"))
            {
                marker.BindText(1, TasksJsonMigrationMarker);
                marker.BindInt64(2, DateTimeOffset.UtcNow.UtcTicks);
                marker.ExpectDone();
            }
            database.Execute("COMMIT;");
        }
        catch
        {
            TryRollback(database);
            throw;
        }
        // The source JSON is deliberately retained unchanged as a user-recoverable migration copy.
    }

    private void MigrateLegacyContactReplyStyles(SqliteDatabase database)
    {
        if (!_legacyContactReplyStylesAvailable) return;
        if (database.ScalarText($"SELECT name FROM migration_state WHERE name='{ContactStylesMigrationMarker}' LIMIT 1;") is not null)
            return;

        var sanitized = SanitizeLegacyContactReplyStyles(_legacyContactReplyStyles);
        database.Execute("BEGIN IMMEDIATE;");
        try
        {
            foreach (var preference in sanitized) InsertContactReplyStyle(database, preference, ignoreExisting: true);
            using (var marker = database.Prepare("INSERT INTO migration_state(name,completed_utc_ticks) VALUES(?1,?2);"))
            {
                marker.BindText(1, ContactStylesMigrationMarker);
                marker.BindInt64(2, DateTimeOffset.UtcNow.UtcTicks);
                marker.ExpectDone();
            }
            database.Execute("COMMIT;");
        }
        catch
        {
            TryRollback(database);
            throw;
        }
        // settings.json is retained unchanged as a recoverable copy; the marker makes SQLite authoritative.
    }

    private IReadOnlyList<ContactReplyStylePreference> ReadContactReplyStylesCore()
    {
        using var database = SqliteDatabase.Open(_path, create: false);
        using var statement = database.Prepare("SELECT contact_name_key,contact_name,style_id,source,updated_utc_ticks "
            + "FROM contact_reply_styles ORDER BY contact_name_key;");
        var result = new List<ContactReplyStylePreference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (statement.StepRow())
        {
            var key = statement.ColumnText(0);
            var name = statement.ColumnText(1);
            var styleId = statement.ColumnText(2);
            var source = statement.ColumnText(3);
            var updatedTicks = statement.ColumnInt64(4);
            if (!ContactReplyStyleCatalog.TryNormalizeContactName(name, out var normalized)
                || !string.Equals(name, normalized, StringComparison.Ordinal)
                || !string.Equals(key, normalized.ToUpperInvariant(), StringComparison.Ordinal)
                || !ContactReplyStyleCatalog.IsSupportedStyle(styleId)
                || !string.Equals(source, ContactReplyStyleCatalog.UserConfirmedSource, StringComparison.Ordinal)
                || updatedTicks < DateTime.MinValue.Ticks || updatedTicks > DateTime.MaxValue.Ticks
                || !seen.Add(normalized))
                throw new InvalidDataException("SQLite 联系人风格偏好包含无效字段；已停止读取偏好。");
            result.Add(new ContactReplyStylePreference(normalized, styleId, source, new DateTimeOffset(updatedTicks, TimeSpan.Zero)));
        }
        return result;
    }

    private void ReplaceContactReplyStylesCore(IReadOnlyList<ContactReplyStylePreference> preferences)
    {
        using var database = SqliteDatabase.Open(_path, create: false);
        database.Execute("BEGIN IMMEDIATE;");
        try
        {
            database.Execute("DELETE FROM contact_reply_styles;");
            foreach (var preference in preferences) InsertContactReplyStyle(database, preference, ignoreExisting: false);
            InsertMigrationMarkerIfMissing(database, ContactStylesMigrationMarker);
            database.Execute("COMMIT;");
        }
        catch
        {
            TryRollback(database);
            throw;
        }
    }

    private static void InsertMigrationMarkerIfMissing(SqliteDatabase database, string markerName)
    {
        using var marker = database.Prepare("INSERT OR IGNORE INTO migration_state(name,completed_utc_ticks) VALUES(?1,?2);");
        marker.BindText(1, markerName);
        marker.BindInt64(2, DateTimeOffset.UtcNow.UtcTicks);
        marker.ExpectDone();
    }

    private static ApprovalAuditRecord CreateApprovalAuditRecord(string actionId, string outcome)
    {
        var isKnownPair = actionId == ApprovalAuditCatalog.MessageSendAction
                && outcome is ApprovalAuditCatalog.Confirmed or ApprovalAuditCatalog.Declined
            || actionId == ApprovalAuditCatalog.CodeTaskAction
                && outcome == ApprovalAuditCatalog.RunDotNetTests
            || actionId == ApprovalAuditCatalog.CodePatchApplyAction
                && outcome == ApprovalAuditCatalog.Confirmed
            || actionId == ApprovalAuditCatalog.FileRecycleAction
                && outcome is ApprovalAuditCatalog.Confirmed or ApprovalAuditCatalog.Declined;
        if (!isKnownPair) throw new ArgumentException("审批审计只接受已登记动作及其固定结果。", nameof(actionId));
        return new ApprovalAuditRecord(Guid.NewGuid(), actionId, outcome, DateTimeOffset.UtcNow);
    }

    private void AppendApprovalAuditCore(ApprovalAuditRecord record)
    {
        using var database = SqliteDatabase.Open(_path, create: false);
        using var statement = database.Prepare("INSERT INTO approval_audit(id,action_id,outcome,created_utc_ticks) "
            + "VALUES(?1,?2,?3,?4);");
        statement.BindText(1, record.Id.ToString("D"));
        statement.BindText(2, record.ActionId);
        statement.BindText(3, record.Outcome);
        statement.BindInt64(4, record.CreatedAtUtc.UtcTicks);
        statement.ExpectDone();
    }

    private IReadOnlyList<ApprovalAuditRecord> ReadRecentApprovalAuditCore(int count)
    {
        if (count == 0) return [];
        using var database = SqliteDatabase.Open(_path, create: false);
        using var statement = database.Prepare("SELECT id,action_id,outcome,created_utc_ticks FROM approval_audit "
            + "ORDER BY created_utc_ticks DESC LIMIT ?1;");
        statement.BindInt32(1, count);
        var result = new List<ApprovalAuditRecord>(count);
        while (statement.StepRow())
        {
            var idText = statement.ColumnText(0);
            var actionId = statement.ColumnText(1);
            var outcome = statement.ColumnText(2);
            var ticks = statement.ColumnInt64(3);
            if (!Guid.TryParse(idText, out var id) || id == Guid.Empty
                || !IsValidApprovalAuditPair(actionId, outcome)
                || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                throw new InvalidDataException("SQLite 审批审计表包含无效字段；已停止读取审计记录。");
            result.Add(new ApprovalAuditRecord(id, actionId, outcome, new DateTimeOffset(ticks, TimeSpan.Zero)));
        }
        return result;
    }

    private static bool IsValidApprovalAuditPair(string actionId, string outcome) =>
        actionId == ApprovalAuditCatalog.MessageSendAction
            ? outcome is ApprovalAuditCatalog.Confirmed or ApprovalAuditCatalog.Declined
            : actionId == ApprovalAuditCatalog.CodeTaskAction
                ? outcome == ApprovalAuditCatalog.RunDotNetTests
                : actionId == ApprovalAuditCatalog.CodePatchApplyAction
                    ? outcome == ApprovalAuditCatalog.Confirmed
                    : actionId == ApprovalAuditCatalog.FileRecycleAction
                        && outcome is ApprovalAuditCatalog.Confirmed or ApprovalAuditCatalog.Declined;

    private static void InsertContactReplyStyle(SqliteDatabase database, ContactReplyStylePreference preference, bool ignoreExisting)
    {
        var conflict = ignoreExisting ? " OR IGNORE" : string.Empty;
        using var statement = database.Prepare($"INSERT{conflict} INTO contact_reply_styles "
            + "(contact_name_key,contact_name,style_id,source,updated_utc_ticks) VALUES(?1,?2,?3,?4,?5);");
        statement.BindText(1, preference.ContactName.ToUpperInvariant());
        statement.BindText(2, preference.ContactName);
        statement.BindText(3, preference.StyleId);
        statement.BindText(4, ContactReplyStyleCatalog.UserConfirmedSource);
        statement.BindInt64(5, preference.UpdatedAtUtc.UtcTicks);
        statement.ExpectDone();
    }

    private static IReadOnlyList<ContactReplyStylePreference> SanitizeLegacyContactReplyStyles(
        IEnumerable<ContactReplyStylePreference> preferences)
    {
        var result = new List<ContactReplyStylePreference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preference in preferences)
        {
            if (preference is null
                || !ContactReplyStyleCatalog.TryNormalizeContactName(preference.ContactName, out var name)
                || !ContactReplyStyleCatalog.IsSupportedStyle(preference.StyleId)
                || !seen.Add(name)) continue;
            result.Add(new ContactReplyStylePreference(name, preference.StyleId,
                ContactReplyStyleCatalog.UserConfirmedSource,
                preference.UpdatedAtUtc == default ? DateTimeOffset.UtcNow : preference.UpdatedAtUtc.ToUniversalTime()));
            if (result.Count == MaximumContactReplyStyles) break;
        }
        return result;
    }

    private static IReadOnlyList<ContactReplyStylePreference> ValidateContactReplyStyles(
        IEnumerable<ContactReplyStylePreference> preferences)
    {
        var result = new List<ContactReplyStylePreference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var preference in preferences)
        {
            if (preference is null
                || !ContactReplyStyleCatalog.TryNormalizeContactName(preference.ContactName, out var name)
                || !ContactReplyStyleCatalog.IsSupportedStyle(preference.StyleId)
                || !string.Equals(preference.Source, ContactReplyStyleCatalog.UserConfirmedSource, StringComparison.Ordinal)
                || preference.UpdatedAtUtc == default
                || !seen.Add(name))
                throw new ArgumentException("联系人风格偏好必须使用唯一有效名称、固定风格、用户确认来源和有效更新时间。", nameof(preferences));
            result.Add(preference with { ContactName = name, UpdatedAtUtc = preference.UpdatedAtUtc.ToUniversalTime() });
            if (result.Count > MaximumContactReplyStyles)
                throw new ArgumentException($"联系人风格偏好最多保存 {MaximumContactReplyStyles} 条。", nameof(preferences));
        }
        return result;
    }

    private void SaveCore(TaskRecord task)
    {
        using var database = SqliteDatabase.Open(_path, create: false);
        using var statement = database.Prepare("INSERT INTO tasks(id,kind,summary,status,created_utc_ticks,updated_utc_ticks,error_code) "
            + "VALUES(?1,?2,?3,?4,?5,?6,?7) ON CONFLICT(id) DO UPDATE SET "
            + "kind=excluded.kind,summary=excluded.summary,status=excluded.status,created_utc_ticks=excluded.created_utc_ticks,"
            + "updated_utc_ticks=excluded.updated_utc_ticks,error_code=excluded.error_code;");
        BindTask(statement, task);
        statement.ExpectDone();
    }

    private IReadOnlyList<TaskRecord> ReadRecentCore(int count)
    {
        if (count == 0) return [];
        using var database = SqliteDatabase.Open(_path, create: false);
        using var statement = database.Prepare("SELECT id,kind,status,created_utc_ticks,updated_utc_ticks,error_code "
            + "FROM tasks ORDER BY updated_utc_ticks DESC LIMIT ?1;");
        statement.BindInt32(1, count);
        var result = new List<TaskRecord>(count);
        while (statement.StepRow())
        {
            if (!Guid.TryParse(statement.ColumnText(0), out var id)
                || !TryCategory(statement.ColumnText(1), out var summary)
                || !Enum.IsDefined(typeof(TaskLifecycleState), statement.ColumnInt32(2)))
                throw new InvalidDataException("SQLite 任务表包含无效的类别或状态；已停止读取历史记录。");

            var createdTicks = statement.ColumnInt64(3);
            var updatedTicks = statement.ColumnInt64(4);
            if (createdTicks < DateTime.MinValue.Ticks || createdTicks > DateTime.MaxValue.Ticks
                || updatedTicks < DateTime.MinValue.Ticks || updatedTicks > DateTime.MaxValue.Ticks)
                throw new InvalidDataException("SQLite 任务表包含无效时间；已停止读取历史记录。");
            var created = new DateTimeOffset(createdTicks, TimeSpan.Zero);
            var updated = new DateTimeOffset(updatedTicks, TimeSpan.Zero);
            var error = SanitizeErrorCode(statement.ColumnNullableText(5));
            var kind = statement.ColumnText(1);
            result.Add(new TaskRecord(id, kind, summary, (TaskLifecycleState)statement.ColumnInt32(2), created, updated,
                Result: null, ErrorCode: error));
        }
        return result;
    }

    private SqlitePersonalDataSummary ReadPersonalDataSummaryCore()
    {
        using var database = SqliteDatabase.Open(_path, create: false);
        var taskCount = database.ScalarInt32("SELECT COUNT(*) FROM tasks;");
        var contactCount = database.ScalarInt32("SELECT COUNT(*) FROM contact_reply_styles;");
        var auditCount = database.ScalarInt32("SELECT COUNT(*) FROM approval_audit;");
        var tasksMarked = database.ScalarInt32($"SELECT COUNT(*) FROM migration_state WHERE name='{TasksJsonMigrationMarker}';") != 0;
        var contactsMarked = database.ScalarInt32($"SELECT COUNT(*) FROM migration_state WHERE name='{ContactStylesMigrationMarker}';") != 0;
        return new(taskCount, contactCount, auditCount, tasksMarked, contactsMarked);
    }

    private bool ClearPersonalDataCore()
    {
        using var database = SqliteDatabase.Open(_path, create: false);
        database.Execute("PRAGMA secure_delete=ON; BEGIN IMMEDIATE;");
        try
        {
            database.Execute("DELETE FROM tasks; DELETE FROM contact_reply_styles; DELETE FROM approval_audit;");
            InsertMigrationMarkerIfMissing(database, TasksJsonMigrationMarker);
            InsertMigrationMarkerIfMissing(database, ContactStylesMigrationMarker);
            database.Execute("COMMIT;");
        }
        catch
        {
            TryRollback(database);
            throw;
        }

        try
        {
            if (!TryTruncateWal(database)) return false;
            database.Execute("VACUUM;");
            return TryTruncateWal(database);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryTruncateWal(SqliteDatabase database)
    {
        using var statement = database.Prepare("PRAGMA wal_checkpoint(TRUNCATE);");
        return statement.StepRow()
            && statement.ColumnInt32(0) == 0
            && statement.ColumnInt32(1) == statement.ColumnInt32(2);
    }

    private void BackupCore(string destination, CancellationToken cancellationToken) =>
        BackupDatabase(_path, destination, cancellationToken);

    private static void BackupDatabase(string sourcePath, string destination, CancellationToken cancellationToken)
    {
        if (File.Exists(destination)) throw new IOException("备份目标已存在；为防止覆盖，已停止备份。");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var reserved = false;
        try
        {
            using (new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) { }
            reserved = true;
            using var source = SqliteDatabase.Open(sourcePath, create: false);
            using var backup = SqliteDatabase.Open(destination, create: true);
            CopyDatabase(source, backup, cancellationToken);
        }
        catch
        {
            if (reserved)
            {
                try { File.Delete(destination); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    private string RestoreBackupCore(string sourcePath, CancellationToken cancellationToken)
    {
        var stagingPath = _path + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp";
        var recoveryPath = CreateRestoreSafetyBackupPath();
        try
        {
            using (var source = SqliteDatabase.Open(sourcePath, create: false))
            {
                ValidateRestorableDatabase(source);
                BackupDatabaseFromOpenDatabase(source, stagingPath, cancellationToken);
            }

            using (var staged = SqliteDatabase.Open(stagingPath, create: false))
                ValidateRestorableDatabase(staged);

            BackupCore(recoveryPath, cancellationToken);
            CheckpointActiveDatabase();
            RemoveCheckpointedSidecars(_path);
            RemoveCheckpointedSidecars(stagingPath);
            File.Replace(stagingPath, _path, destinationBackupFileName: null);
            return recoveryPath;
        }
        catch (Exception ex)
        {
            TryDeleteStagingFiles(stagingPath);
            if (File.Exists(recoveryPath))
            {
                if (ex is OperationCanceledException)
                    throw new OperationCanceledException(
                        $"恢复已取消；恢复前的活动数据库保护副本保存在：{recoveryPath}", ex, cancellationToken);
                throw new IOException($"数据库恢复未完成；恢复前的活动数据库保护副本保存在：{recoveryPath}", ex);
            }
            throw;
        }
    }

    private static void BackupDatabaseFromOpenDatabase(SqliteDatabase source, string destination,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destination)) throw new IOException("恢复暂存目标已存在；为防止覆盖，已停止恢复。");
        using (new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)) { }
        try
        {
            using var staging = SqliteDatabase.Open(destination, create: true);
            CopyDatabase(source, staging, cancellationToken);
        }
        catch
        {
            TryDeleteStagingFiles(destination);
            throw;
        }
    }

    private static void ValidateRestorableDatabase(SqliteDatabase database)
    {
        if (database.ScalarInt32("PRAGMA user_version;") != CurrentSchemaVersion
            || !string.Equals(database.ScalarText("PRAGMA integrity_check;"), "ok", StringComparison.Ordinal))
            throw new InvalidDataException("恢复文件不是完整且受支持的 SQLite v5 备份；活动数据库未替换。");

        if (database.ScalarInt32("SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%';") != 4
            || database.ScalarInt32("SELECT COUNT(*) FROM sqlite_schema WHERE type IN ('trigger','view');") != 0
            || database.ScalarInt32("SELECT COUNT(*) FROM sqlite_schema WHERE type='index' AND sql IS NOT NULL;") != 2
            || database.ScalarInt32("SELECT COUNT(*) FROM sqlite_schema WHERE type='index' AND name IN "
                + "('ix_tasks_updated','ix_approval_audit_created') AND sql IS NOT NULL;") != 2
            || database.ScalarInt32("SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name IN "
                + "('tasks','migration_state','contact_reply_styles','approval_audit');") != 4)
            throw new InvalidDataException("恢复文件含有未知数据库结构；活动数据库未替换。");

        ValidateColumnLayout(database, "tasks", "id,kind,summary,status,created_utc_ticks,updated_utc_ticks,error_code");
        ValidateColumnLayout(database, "migration_state", "name,completed_utc_ticks");
        ValidateColumnLayout(database, "contact_reply_styles", "contact_name_key,contact_name,style_id,source,updated_utc_ticks");
        ValidateColumnLayout(database, "approval_audit", "id,action_id,outcome,created_utc_ticks");
        var auditSql = database.ScalarText("SELECT sql FROM sqlite_schema WHERE type='table' AND name='approval_audit';");
        if (auditSql is null || !auditSql.Contains("'code.patch.apply.v1' AND outcome='confirmed'", StringComparison.Ordinal)
            || !auditSql.Contains("'file.delete.recycle-bin.v1' AND outcome IN ('confirmed','declined')", StringComparison.Ordinal))
            throw new InvalidDataException("恢复文件的审批审计约束与 SQLite v5 不匹配；活动数据库未替换。");
    }

    private static void ValidateColumnLayout(SqliteDatabase database, string table, string expectedColumns)
    {
        var columns = database.ScalarText($"SELECT group_concat(name,',') FROM "
            + $"(SELECT name FROM pragma_table_info('{table}') ORDER BY cid);");
        if (!string.Equals(columns, expectedColumns, StringComparison.Ordinal))
            throw new InvalidDataException("恢复文件的表结构与当前版本不兼容；活动数据库未替换。");
    }

    private void CheckpointActiveDatabase()
    {
        using var database = SqliteDatabase.Open(_path, create: false);
        using var statement = database.Prepare("PRAGMA wal_checkpoint(TRUNCATE);");
        if (!statement.StepRow() || statement.ColumnInt32(0) != 0 || statement.ColumnInt32(1) != 0)
            throw new IOException("活动数据库仍有未检查点 WAL 内容；恢复已停止，原数据库仍可用。");
    }

    private static void RemoveCheckpointedSidecars(string databasePath)
    {
        var walPath = databasePath + "-wal";
        if (File.Exists(walPath))
        {
            if (new FileInfo(walPath).Length != 0)
                throw new IOException("SQLite WAL 文件在检查点后仍有内容；为保护数据库，恢复已停止。");
            File.Delete(walPath);
        }
        var sharedMemoryPath = databasePath + "-shm";
        if (File.Exists(sharedMemoryPath)) File.Delete(sharedMemoryPath);
    }

    private string CreateRestoreSafetyBackupPath()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var suffix = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")[..8];
            var candidate = _path + ".before-restore-" + suffix + ".bak";
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("无法为恢复操作生成不冲突的活动数据库保护备份路径。");
    }

    private static void TryDeleteStagingFiles(string path)
    {
        foreach (var candidate in new[] { path, path + "-wal", path + "-shm" })
        {
            try { if (File.Exists(candidate)) File.Delete(candidate); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private string CreateMigrationBackupPath()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var suffix = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")[..8];
            var candidate = _path + ".before-migration-" + suffix + ".bak";
            if (!File.Exists(candidate)) return candidate;
        }
        throw new IOException("无法为 SQLite 迁移生成不冲突的备份路径。");
    }

    private static void CopyDatabase(SqliteDatabase source, SqliteDatabase destination, CancellationToken cancellationToken)
    {
        var handle = NativeSqlite.sqlite3_backup_init(destination.Handle, "main", source.Handle, "main");
        if (handle == IntPtr.Zero) throw source.CreateException("无法开始 SQLite 一致性备份");

        var finished = false;
        var started = Stopwatch.GetTimestamp();
        var maximumDuration = TimeSpan.FromSeconds(30);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = NativeSqlite.sqlite3_backup_step(handle, -1);
                if (result == NativeSqlite.Done) break;
                if (result is NativeSqlite.Ok or NativeSqlite.Busy or NativeSqlite.Locked)
                {
                    if (result != NativeSqlite.Ok)
                    {
                        if (Stopwatch.GetElapsedTime(started) >= maximumDuration)
                            throw new TimeoutException("SQLite 数据库被其他进程占用超过 30 秒；备份已停止。");
                        Thread.Sleep(50);
                    }
                    continue;
                }
                throw source.CreateException("SQLite 一致性备份失败", result);
            }

            var finishResult = NativeSqlite.sqlite3_backup_finish(handle);
            finished = true;
            if (finishResult != NativeSqlite.Ok) throw source.CreateException("SQLite 备份完成时出错", finishResult);
        }
        finally
        {
            if (!finished) _ = NativeSqlite.sqlite3_backup_finish(handle);
        }
    }

    private static void InsertOrReplace(SqliteDatabase database, TaskRecord task)
    {
        using var statement = database.Prepare("INSERT OR IGNORE INTO tasks(id,kind,summary,status,created_utc_ticks,updated_utc_ticks,error_code) "
            + "VALUES(?1,?2,?3,?4,?5,?6,?7);");
        BindTask(statement, task);
        statement.ExpectDone();
    }

    private static void BindTask(SqliteStatement statement, TaskRecord task)
    {
        statement.BindText(1, task.Id.ToString("D"));
        statement.BindText(2, task.Kind);
        statement.BindText(3, task.Summary);
        statement.BindInt32(4, (int)task.Status);
        statement.BindInt64(5, task.CreatedAtUtc.UtcTicks);
        statement.BindInt64(6, task.UpdatedAtUtc.UtcTicks);
        statement.BindNullableText(7, task.ErrorCode);
    }

    private static TaskRecord Sanitize(TaskRecord task)
    {
        if (!TrySanitize(task, out var sanitized))
            throw new ArgumentException("任务状态不符合本地存储的允许类别、字段长度或状态格式。", nameof(task));
        return sanitized;
    }

    private static bool TrySanitize(TaskRecord task, out TaskRecord sanitized)
    {
        sanitized = task;
        if (task.Id == Guid.Empty || !TryCategory(task.Kind, out var summary)
            || !Enum.IsDefined(typeof(TaskLifecycleState), task.Status)
            || task.CreatedAtUtc == default || task.UpdatedAtUtc == default)
            return false;

        sanitized = task with
        {
            Summary = summary,
            Result = null,
            ErrorCode = SanitizeErrorCode(task.ErrorCode)
        };
        return true;
    }

    private static bool TryCategory(string? kind, out string summary)
    {
        return TaskCategoryCatalog.TryGetLabel(kind, out summary);
    }

    private static string? SanitizeErrorCode(string? errorCode)
    {
        if (string.IsNullOrEmpty(errorCode) || errorCode.Length > 80
            || errorCode.Any(character => !(char.IsAsciiLetterUpper(character) || char.IsAsciiDigit(character) || character == '_')))
            return null;
        return errorCode;
    }

    private static string ValidateDatabasePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("SQLite 文件必须位于本机绝对路径。", nameof(path));
        var fullPath = Path.GetFullPath(path);
        if (string.IsNullOrWhiteSpace(Path.GetFileName(fullPath)) || Path.GetDirectoryName(fullPath) is null)
            throw new ArgumentException("SQLite 文件路径无效。", nameof(path));
        return fullPath;
    }

    private static void TryRollback(SqliteDatabase database)
    {
        try { database.Execute("ROLLBACK;"); }
        catch (InvalidOperationException) { }
    }
}

public sealed record SqlitePersonalDataSummary(
    int TaskRows,
    int ContactPreferenceRows,
    int ApprovalAuditRows,
    bool TasksMigrationMarked,
    bool ContactStylesMigrationMarked);

internal sealed class SqliteDatabase : IDisposable
{
    private const int OpenReadWrite = 0x00000002;
    private const int OpenCreate = 0x00000004;
    private const int OpenFullMutex = 0x00010000;
    private IntPtr _handle;

    private SqliteDatabase(IntPtr handle) => _handle = handle;
    public IntPtr Handle => _handle != IntPtr.Zero ? _handle : throw new ObjectDisposedException(nameof(SqliteDatabase));

    public static SqliteDatabase Open(string path, bool create)
    {
        var flags = OpenReadWrite | OpenFullMutex | (create ? OpenCreate : 0);
        var result = NativeSqlite.sqlite3_open_v2(path, out var handle, flags, IntPtr.Zero);
        if (result != NativeSqlite.Ok)
        {
            var exception = handle == IntPtr.Zero
                ? new IOException($"无法打开 SQLite 文件（错误码 {result}）。")
                : new IOException(ErrorMessage(handle, result));
            if (handle != IntPtr.Zero) _ = NativeSqlite.sqlite3_close_v2(handle);
            throw exception;
        }

        var database = new SqliteDatabase(handle);
        _ = NativeSqlite.sqlite3_busy_timeout(handle, 5000);
        return database;
    }

    public void Execute(string sql)
    {
        var result = NativeSqlite.sqlite3_exec(Handle, sql, IntPtr.Zero, IntPtr.Zero, out var error);
        if (result == NativeSqlite.Ok) return;
        string message;
        if (error != IntPtr.Zero)
        {
            message = Marshal.PtrToStringUTF8(error) ?? ErrorMessage(Handle, result);
            NativeSqlite.sqlite3_free(error);
        }
        else message = ErrorMessage(Handle, result);
        throw new InvalidOperationException($"SQLite 操作失败（错误码 {result}）：{message}");
    }

    public SqliteStatement Prepare(string sql)
    {
        var result = NativeSqlite.sqlite3_prepare_v2(Handle, sql, -1, out var statement, IntPtr.Zero);
        if (result != NativeSqlite.Ok) throw CreateException("无法准备 SQLite 语句", result);
        return new SqliteStatement(this, statement);
    }

    public int ScalarInt32(string sql)
    {
        using var statement = Prepare(sql);
        return statement.StepRow() ? statement.ColumnInt32(0) : 0;
    }

    public string? ScalarText(string sql)
    {
        using var statement = Prepare(sql);
        return statement.StepRow() ? statement.ColumnNullableText(0) : null;
    }

    public IOException CreateException(string operation, int? result = null) =>
        new($"{operation}（SQLite 错误码 {result ?? NativeSqlite.sqlite3_errcode(Handle)}）：{ErrorMessage(Handle, result ?? NativeSqlite.sqlite3_errcode(Handle))}");

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero && NativeSqlite.sqlite3_close_v2(handle) != NativeSqlite.Ok)
            throw new IOException("关闭 SQLite 数据库失败。");
    }

    private static string ErrorMessage(IntPtr handle, int result) =>
        Marshal.PtrToStringUTF8(NativeSqlite.sqlite3_errmsg(handle)) ?? $"SQLite 错误码 {result}";
}

internal sealed class SqliteStatement : IDisposable
{
    private readonly SqliteDatabase _database;
    private IntPtr _handle;

    public SqliteStatement(SqliteDatabase database, IntPtr handle)
    {
        _database = database;
        _handle = handle;
    }

    public void BindText(int index, string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("SQLite 文本字段不能包含 NUL。", nameof(value));
        Check(NativeSqlite.sqlite3_bind_text(Handle, index, value, -1, NativeSqlite.Transient), "绑定文本");
    }

    public void BindNullableText(int index, string? value)
    {
        if (value is null) Check(NativeSqlite.sqlite3_bind_null(Handle, index), "绑定空值");
        else BindText(index, value);
    }

    public void BindInt32(int index, int value) => Check(NativeSqlite.sqlite3_bind_int(Handle, index, value), "绑定整数");
    public void BindInt64(int index, long value) => Check(NativeSqlite.sqlite3_bind_int64(Handle, index, value), "绑定长整数");

    public bool StepRow()
    {
        var result = NativeSqlite.sqlite3_step(Handle);
        if (result == NativeSqlite.Row) return true;
        if (result == NativeSqlite.Done) return false;
        throw _database.CreateException("读取 SQLite 记录失败", result);
    }

    public void ExpectDone()
    {
        var result = NativeSqlite.sqlite3_step(Handle);
        if (result != NativeSqlite.Done) throw _database.CreateException("写入 SQLite 记录失败", result);
    }

    public string ColumnText(int index) => ColumnNullableText(index) ?? "";

    public string? ColumnNullableText(int index)
    {
        var pointer = NativeSqlite.sqlite3_column_text(Handle, index);
        if (pointer == IntPtr.Zero) return null;
        var byteCount = NativeSqlite.sqlite3_column_bytes(Handle, index);
        if (byteCount < 0 || byteCount > 1_000_000) throw new InvalidDataException("SQLite 文本列超出读取上限。");
        var bytes = new byte[byteCount];
        if (byteCount > 0) Marshal.Copy(pointer, bytes, 0, byteCount);
        return Encoding.UTF8.GetString(bytes);
    }

    public int ColumnInt32(int index) => NativeSqlite.sqlite3_column_int(Handle, index);
    public long ColumnInt64(int index) => NativeSqlite.sqlite3_column_int64(Handle, index);

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero) _ = NativeSqlite.sqlite3_finalize(handle);
    }

    private IntPtr Handle => _handle != IntPtr.Zero ? _handle : throw new ObjectDisposedException(nameof(SqliteStatement));

    private void Check(int result, string operation)
    {
        if (result != NativeSqlite.Ok) throw _database.CreateException($"SQLite {operation}失败", result);
    }
}

internal static class NativeSqlite
{
    public const int Ok = 0;
    public const int Busy = 5;
    public const int Locked = 6;
    public const int Row = 100;
    public const int Done = 101;
    public static readonly IntPtr Transient = new(-1);

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_libversion_number")]
    public static extern int sqlite3_libversion_number();
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_open_v2")]
    public static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out IntPtr database, int flags, IntPtr vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_close_v2")]
    public static extern int sqlite3_close_v2(IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_errmsg")]
    public static extern IntPtr sqlite3_errmsg(IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_errcode")]
    public static extern int sqlite3_errcode(IntPtr database);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_exec")]
    public static extern int sqlite3_exec(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        IntPtr callback, IntPtr argument, out IntPtr errorMessage);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_free")]
    public static extern void sqlite3_free(IntPtr pointer);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_prepare_v2")]
    public static extern int sqlite3_prepare_v2(IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        int byteCount, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_text")]
    public static extern int sqlite3_bind_text(IntPtr statement, int index,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int byteCount, IntPtr destructor);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_null")]
    public static extern int sqlite3_bind_null(IntPtr statement, int index);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_int")]
    public static extern int sqlite3_bind_int(IntPtr statement, int index, int value);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_bind_int64")]
    public static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_step")]
    public static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_finalize")]
    public static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_text")]
    public static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_bytes")]
    public static extern int sqlite3_column_bytes(IntPtr statement, int index);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_int")]
    public static extern int sqlite3_column_int(IntPtr statement, int index);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_column_int64")]
    public static extern long sqlite3_column_int64(IntPtr statement, int index);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_busy_timeout")]
    public static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_init")]
    public static extern IntPtr sqlite3_backup_init(IntPtr destination, [MarshalAs(UnmanagedType.LPUTF8Str)] string destinationName,
        IntPtr source, [MarshalAs(UnmanagedType.LPUTF8Str)] string sourceName);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_step")]
    public static extern int sqlite3_backup_step(IntPtr backup, int pages);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sqlite3_backup_finish")]
    public static extern int sqlite3_backup_finish(IntPtr backup);
}
