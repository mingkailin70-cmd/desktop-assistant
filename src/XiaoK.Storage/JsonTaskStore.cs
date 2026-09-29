using System.Text.Json;
using XiaoK.Core;

namespace XiaoK.Storage;

/// <summary>Temporary package-free store. User data stays outside the repository; replace with SQLite before release.</summary>
public sealed class JsonTaskStore : ITaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonTaskStore(string path) => _path = Path.GetFullPath(path);

    public async Task SaveAsync(TaskRecord task, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var all = await ReadAllAsync(cancellationToken);
            var index = all.FindIndex(item => item.Id == task.Id);
            if (index >= 0) all[index] = task; else all.Add(task);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            await using (var stream = File.Create(temp))
                await JsonSerializer.SerializeAsync(stream, all, JsonOptions, cancellationToken);
            File.Move(temp, _path, overwrite: true);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<TaskRecord>> GetRecentAsync(int count, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { return (await ReadAllAsync(cancellationToken)).OrderByDescending(x => x.UpdatedAtUtc).Take(Math.Clamp(count, 0, 100)).ToArray(); }
        finally { _gate.Release(); }
    }

    private async Task<List<TaskRecord>> ReadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<TaskRecord>>(stream, JsonOptions, cancellationToken) ?? [];
    }
}
