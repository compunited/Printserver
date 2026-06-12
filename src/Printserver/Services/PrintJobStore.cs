using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Printserver.Models;

namespace Printserver.Services;

public interface IPrintJobStore
{
    Task<IReadOnlyCollection<PrintJob>> GetAllAsync();
    Task<PrintJob?> GetAsync(Guid id);
    Task UpsertAsync(PrintJob job);
    Task<PrintJob?> UpdateStatusAsync(Guid id, PrintJobStatus status);
    Task<bool> RemoveAsync(Guid id);
}

public sealed class FileBackedPrintJobStore : IPrintJobStore
{
    private readonly ConcurrentDictionary<Guid, PrintJob> _jobs = new();
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly string _storagePath;

    public FileBackedPrintJobStore(IOptions<PrintServerOptions> options)
    {
        var dataDir = options.Value.DataDirectory;
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        }

        Directory.CreateDirectory(dataDir);
        _storagePath = Path.Combine(dataDir, "jobs.json");
        LoadFromDisk();
    }

    public Task<IReadOnlyCollection<PrintJob>> GetAllAsync()
        => Task.FromResult<IReadOnlyCollection<PrintJob>>(_jobs.Values.OrderByDescending(job => job.CreatedAt).ToList());

    public Task<PrintJob?> GetAsync(Guid id)
        => Task.FromResult(_jobs.TryGetValue(id, out var job) ? job : null);

    public async Task UpsertAsync(PrintJob job)
    {
        _jobs[job.Id] = job;
        await PersistAsync();
    }

    public async Task<PrintJob?> UpdateStatusAsync(Guid id, PrintJobStatus status)
    {
        if (!_jobs.TryGetValue(id, out var job))
        {
            return null;
        }

        var updated = job with { Status = status, UpdatedAt = DateTimeOffset.UtcNow };
        _jobs[id] = updated;
        await PersistAsync();
        return updated;
    }

    public async Task<bool> RemoveAsync(Guid id)
    {
        var removed = _jobs.TryRemove(id, out _);
        if (removed)
        {
            await PersistAsync();
        }

        return removed;
    }

    private void LoadFromDisk()
    {
        if (!File.Exists(_storagePath))
        {
            return;
        }

        var json = File.ReadAllText(_storagePath);
        var jobs = JsonSerializer.Deserialize<List<PrintJob>>(json, SerializerOptions);
        if (jobs is null)
        {
            return;
        }

        foreach (var job in jobs)
        {
            _jobs[job.Id] = job;
        }
    }

    private async Task PersistAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(_jobs.Values, SerializerOptions);
            await File.WriteAllTextAsync(_storagePath, json);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };
}


