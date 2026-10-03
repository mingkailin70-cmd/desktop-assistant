using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XiaoK.Storage;

public sealed record LegacySettingsCleanupSnapshot(
    bool SettingsFileExists,
    bool HasContactStylesProperty,
    int ContactStyleRows,
    int PetWindowPositionPropertyCount,
    long Length,
    long LastWriteUtcTicks,
    string Sha256);

public sealed record ManagedPrivacyFileSnapshot(string FullPath, long Length, long LastWriteUtcTicks);

public sealed record ManagedPrivacyFilesPlan(
    string RootPath,
    string LegacyTasksJsonPath,
    IReadOnlyList<ManagedPrivacyFileSnapshot> Files,
    int SkippedEntries)
{
    public long TotalBytes => Files.Sum(file => file.Length);
}

public sealed record ManagedPrivacyFilesDeleteResult(int DeletedCount, IReadOnlyList<string> FailedFileNames, bool PlanChanged = false);

/// <summary>Removes only known legacy personal-preference fields from the current settings file.</summary>
public static class LegacySettingsPrivacyCleanup
{
    private const int MaximumSettingsBytes = 2 * 1024 * 1024;
    private const string ContactStylesProperty = "contactReplyStyles";
    private static readonly string[] PetWindowPositionProperties =
        ["petWindowLeft", "petWindowTop", "petWindowLeftPixels", "petWindowTopPixels"];

    public static LegacySettingsCleanupSnapshot Preview(string settingsPath)
    {
        var path = ValidateLocalPath(settingsPath);
        if (!File.Exists(path)) return new(false, false, 0, 0, 0, 0, "");
        var (bytes, length, lastWriteTicks) = ReadStableSettingsFile(path);
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("本机设置文件不是 JSON 对象；为避免改坏设置，已停止清理。");

        var properties = document.RootElement.EnumerateObject().ToArray();
        var matchingProperties = properties
            .Where(property => string.Equals(property.Name, ContactStylesProperty, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matchingProperties.Length > 1)
            throw new InvalidDataException("本机设置文件重复定义了联系人偏好字段；为避免误删，已停止清理。");

        var positionProperties = properties.Where(property => IsPetWindowPositionProperty(property.Name)).ToArray();
        foreach (var knownProperty in PetWindowPositionProperties)
            if (positionProperties.Count(property => string.Equals(property.Name, knownProperty, StringComparison.OrdinalIgnoreCase)) > 1)
                throw new InvalidDataException("本机设置文件重复定义了桌宠坐标字段；为避免误删，已停止清理。");

        var contactCount = 0;
        if (matchingProperties.Length == 1)
        {
            var value = matchingProperties[0].Value;
            if (value.ValueKind == JsonValueKind.Array) contactCount = value.GetArrayLength();
            else if (value.ValueKind != JsonValueKind.Null)
                throw new InvalidDataException("本机设置文件中的旧联系人偏好格式无法确认；为避免误删，已停止清理。");
        }

        return new(true, matchingProperties.Length == 1, contactCount, positionProperties.Length, length, lastWriteTicks,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public static bool RemoveIfUnchanged(string settingsPath, LegacySettingsCleanupSnapshot approvedSnapshot)
    {
        ArgumentNullException.ThrowIfNull(approvedSnapshot);
        var path = ValidateLocalPath(settingsPath);
        if (!approvedSnapshot.SettingsFileExists
            || (!approvedSnapshot.HasContactStylesProperty && approvedSnapshot.PetWindowPositionPropertyCount == 0)) return false;

        var (bytes, length, lastWriteTicks) = ReadStableSettingsFile(path);
        if (length != approvedSnapshot.Length || lastWriteTicks != approvedSnapshot.LastWriteUtcTicks
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(approvedSnapshot.Sha256)))
            throw new InvalidOperationException("本机设置文件在确认期间发生变化；没有清理，请重新预览。");

        var root = JsonNode.Parse(bytes) as JsonObject
            ?? throw new InvalidDataException("本机设置文件不是 JSON 对象；没有清理。");
        var matchingKeys = root.Select(property => property.Key)
            .Where(IsLegacyPersonalSettingProperty)
            .ToArray();
        var contactKeyCount = matchingKeys.Count(key => string.Equals(key, ContactStylesProperty, StringComparison.OrdinalIgnoreCase));
        var positionKeyCount = matchingKeys.Length - contactKeyCount;
        if (contactKeyCount != (approvedSnapshot.HasContactStylesProperty ? 1 : 0)
            || positionKeyCount != approvedSnapshot.PetWindowPositionPropertyCount)
            throw new InvalidOperationException("本机设置文件结构与预览不一致；没有清理，请重新预览。");
        foreach (var key in matchingKeys) root.Remove(key);

        var temporaryPath = path + ".privacy-cleanup-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(output, root, options);
                output.Flush(flushToDisk: true);
            }

            var (latestBytes, latestLength, latestWriteTicks) = ReadStableSettingsFile(path);
            if (latestLength != approvedSnapshot.Length || latestWriteTicks != approvedSnapshot.LastWriteUtcTicks
                || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(latestBytes), Convert.FromHexString(approvedSnapshot.Sha256)))
                throw new InvalidOperationException("本机设置文件在清理写入期间发生变化；没有替换原文件。");

            File.Replace(temporaryPath, path, destinationBackupFileName: null);
            return true;
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool IsPetWindowPositionProperty(string name) =>
        PetWindowPositionProperties.Any(property => string.Equals(name, property, StringComparison.OrdinalIgnoreCase));

    private static bool IsLegacyPersonalSettingProperty(string name) =>
        string.Equals(name, ContactStylesProperty, StringComparison.OrdinalIgnoreCase)
        || IsPetWindowPositionProperty(name);

    private static (byte[] Bytes, long Length, long LastWriteTicks) ReadStableSettingsFile(string path)
    {
        RejectReparseFile(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaximumSettingsBytes)
            throw new InvalidDataException("本机设置文件超过2 MiB；为避免高内存读取，已停止清理。");
        var length = input.Length;
        var bytes = new byte[checked((int)length)];
        input.ReadExactly(bytes);
        var lastWriteTicks = File.GetLastWriteTimeUtc(path).Ticks;
        if (new FileInfo(path).Length != length)
            throw new IOException("本机设置文件在读取时发生变化；请重新打开设置页后重试。");
        return (bytes, length, lastWriteTicks);
    }

    private static string ValidateLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("设置文件必须使用本机绝对路径。", nameof(path));
        return Path.GetFullPath(path);
    }

    private static void RejectReparseFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("本机设置文件是链接或重解析点；为避免越界，已停止清理。");
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("本机设置文件路径包含链接或重解析点；为避免越界，已停止清理。");
    }
}

/// <summary>Plans deletion of only known XiaoK migration, restore, temporary, and task-history files.</summary>
public static class ManagedPrivacyFileCleanup
{
    public static ManagedPrivacyFilesPlan Preview(string dataRoot, string legacyTasksJsonPath)
    {
        var rootPath = ValidateLocalPath(dataRoot);
        var legacyPath = ValidateLocalPath(legacyTasksJsonPath);
        if (!string.Equals(Path.GetDirectoryName(legacyPath)?.TrimEnd(Path.DirectorySeparatorChar), rootPath.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(legacyPath), "tasks.json", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("旧任务文件路径必须是数据目录中的 tasks.json。", nameof(legacyTasksJsonPath));

        if (!Directory.Exists(rootPath)) return new(rootPath, legacyPath, [], 0);
        EnsureNoReparsePointsInPath(rootPath);
        var files = new List<ManagedPrivacyFileSnapshot>();
        var skipped = 0;
        foreach (var path in Directory.EnumerateFiles(rootPath, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (!IsManagedPrivacyFile(name, legacyPath)) continue;

            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.ReadOnly) != 0)
            {
                skipped++;
                continue;
            }

            var info = new FileInfo(path);
            files.Add(new(path, info.Length, info.LastWriteTimeUtc.Ticks));
        }

        return new(rootPath, legacyPath, files.OrderBy(file => file.FullPath, StringComparer.OrdinalIgnoreCase).ToArray(), skipped);
    }

    public static ManagedPrivacyFilesDeleteResult DeleteIfUnchanged(ManagedPrivacyFilesPlan approvedPlan)
    {
        ArgumentNullException.ThrowIfNull(approvedPlan);
        ManagedPrivacyFilesPlan current;
        try { current = Preview(approvedPlan.RootPath, approvedPlan.LegacyTasksJsonPath); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(0, approvedPlan.Files.Select(file => Path.GetFileName(file.FullPath)).ToArray(), PlanChanged: true);
        }
        if (current.SkippedEntries != approvedPlan.SkippedEntries || !current.Files.SequenceEqual(approvedPlan.Files))
            return new(0, approvedPlan.Files.Select(file => Path.GetFileName(file.FullPath)).ToArray(), PlanChanged: true);

        var deleted = 0;
        var failures = new List<string>();
        foreach (var file in current.Files)
        {
            try
            {
                var attributes = File.GetAttributes(file.FullPath);
                var info = new FileInfo(file.FullPath);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0
                    || info.Length != file.Length || info.LastWriteTimeUtc.Ticks != file.LastWriteUtcTicks)
                {
                    failures.Add(Path.GetFileName(file.FullPath));
                    continue;
                }
                File.Delete(file.FullPath);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(Path.GetFileName(file.FullPath));
            }
        }
        return new(deleted, failures);
    }

    private static bool IsManagedPrivacyFile(string name, string legacyTasksJsonPath)
    {
        if (string.Equals(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(legacyTasksJsonPath)!, name)),
            legacyTasksJsonPath, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(name, PetWindowPositionStore.FileName, StringComparison.OrdinalIgnoreCase)) return true;
        return name.StartsWith("tasks.sqlite3.before-migration-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("tasks.sqlite3.before-restore-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("tasks.sqlite3.restore-", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("xiaok-backup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase);
    }

    private static string ValidateLocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
            || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("清理目录和文件必须使用本机绝对路径。", nameof(path));
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var driveRoot = Path.GetPathRoot(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrEmpty(Path.GetFileName(fullPath)) || string.Equals(fullPath, driveRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("清理目录不能是磁盘根目录。", nameof(path));
        return fullPath;
    }

    private static void EnsureNoReparsePointsInPath(string fullPath)
    {
        for (var current = new DirectoryInfo(fullPath); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("小K数据目录包含链接或重解析点；为避免越界，已停止清理。");
    }
}
