using System.Collections.Concurrent;

namespace Printserver.Services;

public sealed class PrintJobQueue
{
    private readonly ConcurrentQueue<Guid> _queue = new();

    public void Enqueue(Guid id) => _queue.Enqueue(id);

    public Guid? TryDequeue() => _queue.TryDequeue(out var id) ? id : null;
}
