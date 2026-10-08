using System.Text.Json;
using XiaoK.Core;

namespace XiaoK.Storage;

public sealed record PetWindowPosition(int LeftPixels, int TopPixels, double ScalePercent = 100);

/// <summary>
/// Keeps the desktop pet's volatile window placement separate from application settings.
/// </summary>
public sealed class PetWindowPositionStore
{
    public const string FileName = "pet-window-position.json";

    private const int MaximumCoordinateMagnitude = 1_000_000;
    private const int MaximumFileBytes = 512;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly string _path;

    public PetWindowPositionStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _path = Path.Combine(_directory, FileName);
    }

    public bool TryLoad(out PetWindowPosition position)
    {
        position = default!;
        try
        {
            if (!File.Exists(_path)) return false;
            var file = new FileInfo(_path);
            if (file.Length is <= 0 or > MaximumFileBytes
                || (file.Attributes & FileAttributes.ReparsePoint) != 0
                || IsReparsePoint(_directory)) return false;

            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 2 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var properties = root.EnumerateObject().ToArray();
            if (properties.Length is not (2 or 3)
                || properties.Count(property => property.NameEquals("leftPixels")) != 1
                || properties.Count(property => property.NameEquals("topPixels")) != 1
                || properties.Any(property => property.Name is not ("leftPixels" or "topPixels" or "scalePercent"))
                || properties.Count(property => property.NameEquals("scalePercent")) > 1
                || !root.TryGetProperty("leftPixels", out var left)
                || !root.TryGetProperty("topPixels", out var top)
                || !left.TryGetInt32(out var leftPixels)
                || !top.TryGetInt32(out var topPixels)
                || !IsCoordinate(leftPixels)
                || !IsCoordinate(topPixels)) return false;

            // 老版仅保存坐标，恢复原来的 100% 尺寸。
            var scalePercent = 100d;
            if (root.TryGetProperty("scalePercent", out var scale)
                && (scale.ValueKind != JsonValueKind.Number || !scale.TryGetDouble(out scalePercent)
                    || !PetSizingPolicy.IsValidPercent(scalePercent))) return false;
            position = new PetWindowPosition(leftPixels, topPixels, scalePercent);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public void Save(int leftPixels, int topPixels, double scalePercent = 100)
    {
        if (!IsCoordinate(leftPixels) || !IsCoordinate(topPixels))
            throw new ArgumentOutOfRangeException(nameof(leftPixels), "桌宠坐标超出可接受范围。");
        if (!PetSizingPolicy.IsValidPercent(scalePercent))
            throw new ArgumentOutOfRangeException(nameof(scalePercent), "桌宠缩放应在 40%–160% 之间。");

        Directory.CreateDirectory(_directory);
        if (IsReparsePoint(_directory) || IsReparsePoint(_path))
            throw new IOException("桌宠位置目录或文件不能是重解析点。");

        var temporaryPath = Path.Combine(_directory, ".pet-window-position-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new PetWindowPosition(leftPixels, topPixels, scalePercent), SerializerOptions);
            if (bytes.Length > MaximumFileBytes) throw new IOException("桌宠位置数据超出大小限制。");
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       bufferSize: 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path)) File.Replace(temporaryPath, _path, destinationBackupFileName: null);
            else File.Move(temporaryPath, _path);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsCoordinate(int value) => value is >= -MaximumCoordinateMagnitude and <= MaximumCoordinateMagnitude;

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
}
