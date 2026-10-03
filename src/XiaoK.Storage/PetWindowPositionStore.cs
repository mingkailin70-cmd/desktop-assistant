using System.Text.Json;

namespace XiaoK.Storage;

public sealed record PetWindowPosition(int LeftPixels, int TopPixels);

/// <summary>
/// Keeps the desktop pet's volatile window placement separate from application settings.
/// </summary>
public sealed class PetWindowPositionStore
{
    private const int MaximumCoordinateMagnitude = 1_000_000;
    private const int MaximumFileBytes = 512;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly string _path;

    public PetWindowPositionStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _path = Path.Combine(_directory, "pet-window-position.json");
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
            if (properties.Length != 2
                || properties.Count(property => property.NameEquals("leftPixels")) != 1
                || properties.Count(property => property.NameEquals("topPixels")) != 1
                || !root.TryGetProperty("leftPixels", out var left)
                || !root.TryGetProperty("topPixels", out var top)
                || !left.TryGetInt32(out var leftPixels)
                || !top.TryGetInt32(out var topPixels)
                || !IsCoordinate(leftPixels)
                || !IsCoordinate(topPixels)) return false;

            position = new PetWindowPosition(leftPixels, topPixels);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public void Save(int leftPixels, int topPixels)
    {
        if (!IsCoordinate(leftPixels) || !IsCoordinate(topPixels))
            throw new ArgumentOutOfRangeException(nameof(leftPixels), "桌宠坐标超出可接受范围。");

        Directory.CreateDirectory(_directory);
        if (IsReparsePoint(_directory) || IsReparsePoint(_path))
            throw new IOException("桌宠位置目录或文件不能是重解析点。");

        var temporaryPath = Path.Combine(_directory, ".pet-window-position-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new PetWindowPosition(leftPixels, topPixels), SerializerOptions);
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
