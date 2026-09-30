using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Runtime.Versioning;

namespace XiaoK.Tools;

internal sealed record AppContainerRecoveryRecord(int Version, string ProfileName, string? AppContainerSid,
    string[] PermissionRoots);

/// <summary>Durably records the exact temporary AppContainer ACL scope so a later Host can recover after a crash.</summary>
internal static class AppContainerRecoveryJournal
{
    private const string ProfilePrefix = "XiaoK.CodeTask.";
    private const int MaximumRecordBytes = 64 * 1024;

    public static string DefaultRoot
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(local)) throw new IOException("无法确定当前用户的本地应用数据目录。");
            return Path.Combine(local, "XiaoK", "AppContainerRecovery");
        }
    }

    [SupportedOSPlatform("windows")]
    public static string PrepareRoot(string? root = null)
    {
        var fullPath = Path.GetFullPath(root ?? DefaultRoot);
        var driveRoot = Path.GetPathRoot(fullPath);
        if (fullPath.StartsWith("\\\\", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(driveRoot)
            || string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), driveRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new IOException("隔离恢复清单目录必须是本地磁盘上的非根目录。");

        EnsureNoReparsePoints(fullPath);
        Directory.CreateDirectory(fullPath);
        EnsureNoReparsePoints(fullPath);

        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("无法确认隔离恢复清单的当前用户身份。");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(fullPath).SetAccessControl(security);
        return fullPath;
    }

    public static FileStream AcquireLock(string root)
    {
        var path = Path.Combine(root, "runner.lock");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("隔离恢复锁文件是重解析点。");
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
    }

    public static string ManifestPath(string root, string profileName)
    {
        ValidateProfileName(profileName);
        return Path.Combine(root, profileName + ".json");
    }

    [SupportedOSPlatform("windows")]
    public static void Write(string root, AppContainerRecoveryRecord record)
    {
        ValidateRecord(record);
        var path = ManifestPath(root, record.ProfileName);
        var temporary = path + ".tmp";
        if (File.Exists(temporary) || Directory.Exists(temporary))
            throw new IOException("发现同名的隔离恢复暂存文件；拒绝覆盖。");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        if (bytes.Length > MaximumRecordBytes) throw new IOException("隔离恢复清单超过大小上限。");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                   4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, destinationBackupFileName: null);
        else File.Move(temporary, path);
    }

    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<(string Path, AppContainerRecoveryRecord Record)> ReadPending(string root)
    {
        var pending = new List<(string, AppContainerRecoveryRecord)>();
        foreach (var temporary in Directory.EnumerateFiles(root, ProfilePrefix + "*.json.tmp", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(temporary) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("隔离恢复暂存文件是重解析点；拒绝访问。");
            File.Delete(temporary);
        }

        foreach (var path in Directory.EnumerateFiles(root, ProfilePrefix + "*.json", SearchOption.TopDirectoryOnly))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("隔离恢复清单是重解析点；拒绝访问。");
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumRecordBytes)
                throw new IOException("隔离恢复清单大小无效；拒绝继续运行隔离命令。");
            var bytes = File.ReadAllBytes(path);
            var record = JsonSerializer.Deserialize<AppContainerRecoveryRecord>(bytes)
                ?? throw new IOException("隔离恢复清单格式无效；拒绝继续运行隔离命令。");
            ValidateRecord(record);
            if (!string.Equals(Path.GetFileName(path), record.ProfileName + ".json", StringComparison.Ordinal))
                throw new IOException("隔离恢复清单文件名与内容不一致。");
            pending.Add((path, record));
        }
        return pending;
    }

    public static void DeleteRecord(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    public static void ValidateProfileName(string profileName)
    {
        if (!profileName.StartsWith(ProfilePrefix, StringComparison.Ordinal)
            || profileName.Length != ProfilePrefix.Length + 32
            || profileName[ProfilePrefix.Length..].Any(character => !char.IsAsciiHexDigit(character)))
            throw new IOException("隔离恢复清单中的 AppContainer 名称无效。");
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateRecord(AppContainerRecoveryRecord record)
    {
        if (record.Version != 1 || record.PermissionRoots is null || record.PermissionRoots.Length is < 1 or > 8)
            throw new IOException("隔离恢复清单版本或权限范围数量无效。");
        ValidateProfileName(record.ProfileName);
        if (record.AppContainerSid is not null)
        {
            var sid = new SecurityIdentifier(record.AppContainerSid);
            if (!sid.Value.StartsWith("S-1-15-2-", StringComparison.Ordinal))
                throw new IOException("隔离恢复清单中的 AppContainer SID 无效。");
        }
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in record.PermissionRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)
                || root.StartsWith("\\\\", StringComparison.Ordinal)
                || string.Equals(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                || !roots.Add(Path.GetFullPath(root)))
                throw new IOException("隔离恢复清单中的 ACL 路径无效或重复。");
        }
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (!current.Exists) continue;
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("隔离恢复清单路径包含重解析点；拒绝访问。");
        }
    }
}
