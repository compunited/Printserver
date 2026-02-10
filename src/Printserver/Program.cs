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

app.MapGet("/printer/profiles", (IOptions<PrintServerOptions> options) =>
{
    return Results.Ok(options.Value.PreheatProfiles);
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


