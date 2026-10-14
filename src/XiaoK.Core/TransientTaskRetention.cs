namespace XiaoK.Core;

/// <summary>Tracks the most recent task IDs without allowing duplicate result updates to consume extra slots.</summary>
internal sealed class TransientTaskRetention
{
    private readonly object _gate = new();
    private readonly int _maximumEntries;
    private readonly LinkedList<Guid> _order = new();
    private readonly Dictionary<Guid, LinkedListNode<Guid>> _nodes = [];

    public TransientTaskRetention(int maximumEntries)
    {
        if (maximumEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        _maximumEntries = maximumEntries;
    }

    public int Count
    {
        get { lock (_gate) return _nodes.Count; }
    }

    public IReadOnlyList<Guid> Snapshot()
    {
        lock (_gate) return _order.ToArray();
    }

    public IReadOnlyList<Guid> Touch(Guid taskId)
    {
        if (taskId == Guid.Empty) throw new ArgumentException("Task ID must be non-empty.", nameof(taskId));

        lock (_gate)
        {
            if (_nodes.Remove(taskId, out var existing)) _order.Remove(existing);
            _nodes.Add(taskId, _order.AddLast(taskId));

            List<Guid>? expired = null;
            while (_nodes.Count > _maximumEntries)
            {
                var oldest = _order.First!;
                _order.RemoveFirst();
                _nodes.Remove(oldest.Value);
                (expired ??= []).Add(oldest.Value);
            }

            return expired ?? [];
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _order.Clear();
            _nodes.Clear();
        }
    }
}
