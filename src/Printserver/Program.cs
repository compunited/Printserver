using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Options;
using Printserver.Models;
using Printserver.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<PrintServerOptions>(builder.Configuration.GetSection("PrintServer"));
builder.Services.Configure<WebcamOptions>(builder.Configuration.GetSection("Webcam"));
builder.Services.AddSingleton<IPrintJobStore, FileBackedPrintJobStore>();
builder.Services.AddSingleton<PrintJobQueue>();
builder.Services.AddSingleton<ISerialConnectionService, SerialConnectionService>();
builder.Services.AddSingleton<IPrinterStateService, PrinterStateService>();
builder.Services.AddHostedService<JobProcessor>();
builder.Services.AddHttpClient();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
app.UseFileServer();

app.MapGet("/api", () => Results.Ok(new
{
    service = "Printserver",
    description = "Minimaler Printserver im Stil eines Repetier-Server-Grundgeruests.",
    version = "0.2.0",
    endpoints = new[]
    {
        "/jobs",
        "/jobs/{id}",
        "/jobs/{id}/gcode",
        "/jobs/{id}/start",
        "/jobs/{id}/complete",
        "/jobs/{id}/cancel",
        "/queue/next",
        "/webcam/snapshot",
        "/webcam/stream"
    }
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapGet("/jobs", async (IPrintJobStore store) => Results.Ok(await store.GetAllAsync()));

app.MapGet("/jobs/{id:guid}", async (Guid id, IPrintJobStore store) =>
{
    var job = await store.GetAsync(id);
    return job is null ? Results.NotFound() : Results.Ok(job);
});

app.MapGet("/jobs/{id:guid}/gcode", async (Guid id, IPrintJobStore store) =>
{
    var job = await store.GetAsync(id);
    return job is null ? Results.NotFound() : Results.Text(job.Content, "text/plain");
});

app.MapPost("/jobs", async (HttpRequest request, IPrintJobStore store, PrintJobQueue queue) =>
{
    var jobRequest = await request.ReadFromJsonAsync<PrintJobRequest>();
    if (jobRequest is null || string.IsNullOrWhiteSpace(jobRequest.Name) || string.IsNullOrWhiteSpace(jobRequest.Content))
    {
        return Results.BadRequest(new { error = "Name und Content muessen gesetzt sein." });
    }

    var job = PrintJob.Create(jobRequest.Name.Trim(), jobRequest.Content);
    await store.UpsertAsync(job);
    queue.Enqueue(job.Id);
    return Results.Created($"/jobs/{job.Id}", job);
});

app.MapPost("/jobs/upload", async (HttpRequest request, IPrintJobStore store, PrintJobQueue queue) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { error = "multipart/form-data erwartet." });
    }

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0)
    {
        return Results.BadRequest(new { error = "Datei fehlt." });
    }

    var name = form["name"].FirstOrDefault() ?? Path.GetFileNameWithoutExtension(file.FileName);
    await using var stream = file.OpenReadStream();
    using var reader = new StreamReader(stream);
    var content = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(content))
    {
        return Results.BadRequest(new { error = "Datei ist leer." });
    }

    var job = PrintJob.Create(name.Trim(), content);
    await store.UpsertAsync(job);
    queue.Enqueue(job.Id);
    return Results.Created($"/jobs/{job.Id}", job);
});

app.MapPost("/jobs/{id:guid}/start", async (Guid id, IPrintJobStore store) =>
{
    var job = await store.UpdateStatusAsync(id, PrintJobStatus.Printing);
    return job is null ? Results.NotFound() : Results.Ok(job);
});

app.MapPost("/jobs/{id:guid}/complete", async (Guid id, IPrintJobStore store) =>
{
    var job = await store.UpdateStatusAsync(id, PrintJobStatus.Completed);
    return job is null ? Results.NotFound() : Results.Ok(job);
});

app.MapPost("/jobs/{id:guid}/cancel", async (Guid id, IPrintJobStore store) =>
{
    var job = await store.UpdateStatusAsync(id, PrintJobStatus.Canceled);
    return job is null ? Results.NotFound() : Results.Ok(job);
});

app.MapDelete("/jobs/{id:guid}", async (Guid id, IPrintJobStore store) =>
{
    var removed = await store.RemoveAsync(id);
    return removed ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/queue/next", async (IPrintJobStore store, PrintJobQueue queue) =>
{
    var next = queue.TryDequeue();
    if (next is null)
    {
        return Results.NoContent();
    }

    var job = await store.GetAsync(next.Value);
    return job is null ? Results.NoContent() : Results.Ok(job);
});

app.MapGet("/webcam/snapshot", (IOptions<WebcamOptions> options) =>
{
    var snapshotPath = options.Value.SnapshotPath;
    if (string.IsNullOrWhiteSpace(snapshotPath))
    {
        return Results.Problem("Webcam Snapshot ist nicht konfiguriert.", statusCode: StatusCodes.Status501NotImplemented);
    }

    if (!File.Exists(snapshotPath))
    {
        return Results.NotFound(new { error = "Snapshot-Datei nicht gefunden." });
    }

    return Results.File(snapshotPath, contentType: "image/jpeg");
});

app.MapGet("/webcam/stream", async (IOptions<WebcamOptions> options, IHttpClientFactory httpClientFactory) =>
{
    var streamUrl = options.Value.MjpegUrl;
    if (string.IsNullOrWhiteSpace(streamUrl))
    {
        return Results.Problem("Webcam Stream ist nicht konfiguriert.", statusCode: StatusCodes.Status501NotImplemented);
    }

    if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri))
    {
        return Results.BadRequest(new { error = "Webcam URL ist ungueltig." });
    }

    var client = httpClientFactory.CreateClient();
    var stream = await client.GetStreamAsync(uri);
    return Results.Stream(stream, contentType: "multipart/x-mixed-replace; boundary=frame");
});

app.Run();

internal sealed class PrintJobQueue
{
    private readonly ConcurrentQueue<Guid> _queue = new();

    public void Enqueue(Guid id) => _queue.Enqueue(id);

    public Guid? TryDequeue() => _queue.TryDequeue(out var id) ? id : null;
}

internal interface IPrintJobStore
{
    Task<IReadOnlyCollection<PrintJob>> GetAllAsync();
    Task<PrintJob?> GetAsync(Guid id);
    Task UpsertAsync(PrintJob job);
    Task<PrintJob?> UpdateStatusAsync(Guid id, PrintJobStatus status);
    Task<bool> RemoveAsync(Guid id);
}

internal sealed class FileBackedPrintJobStore : IPrintJobStore
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

internal sealed record PrintJob(
    Guid Id,
    string Name,
    string Content,
    PrintJobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static PrintJob Create(string name, string content) => new(
        Guid.NewGuid(),
        name,
        content,
        PrintJobStatus.Queued,
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);
}

internal sealed record PrintJobRequest(string Name, string Content);

internal enum PrintJobStatus
{
    Queued,
    Printing,
    Completed,
    Canceled
}

internal sealed class PrintServerOptions
{
    public string? DataDirectory { get; init; }
}

internal sealed class WebcamOptions
{
    public string? SnapshotPath { get; init; }
    public string? MjpegUrl { get; init; }
}

    return Results.Stream(stream, contentType: "multipart/x-mixed-replace; boundary=frame");
});

app.MapGet("/printer/profiles", (IOptions<PrintServerOptions> options) =>
{
    return Results.Ok(options.Value.PreheatProfiles);
});

app.MapGet("/printer/status", (IPrinterStateService state) =>
{
    return Results.Ok(new 
    { 
        state.IsPrinting, 
        state.CurrentJobId, 
        state.Progress, 
        state.TotalLines, 
        state.ProcessedLines 
    });
});

app.MapPost("/printer/command", async (HttpRequest request, ISerialConnectionService serialService, IPrinterStateService printerState, CancellationToken cancellationToken) =>
{
    var commandRequest = await request.ReadFromJsonAsync<PrinterCommandRequest>(cancellationToken: cancellationToken);
    
    if (commandRequest is null || string.IsNullOrWhiteSpace(commandRequest.Command))
    {
        return Results.BadRequest(new { error = "Command cannot be empty." });
    }

    if (printerState.IsPrinting)
    {
        return Results.Conflict(new { error = "Printer is busy with a job." });
    }

    if (!serialService.IsConnected)
    {
        return Results.BadRequest(new { error = "Printer not connected." });
    }

    try
    {
        // TODO: Ideally verify if a job is printing to prevent interference.
        // For now, we assume the user knows what they are doing.
        
        await serialService.SendGCodeLineAsync(commandRequest.Command, cancellationToken);
        return Results.Ok(new { status = "sent", command = commandRequest.Command });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

app.Run();


