using Printserver.Models;

namespace Printserver.Services;

public class JobProcessor : BackgroundService
{
    private readonly ILogger<JobProcessor> _logger;
    private readonly PrintJobQueue _queue;
    private readonly IPrintJobStore _store;
    private readonly ISerialConnectionService _serialService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPrinterStateService _printerState;

    public JobProcessor(
        ILogger<JobProcessor> logger, 
        PrintJobQueue queue, 
        IServiceScopeFactory scopeFactory,
        ISerialConnectionService serialService,
        IPrintJobStore store,
        IPrinterStateService printerState) 
    {
        _logger = logger;
        _queue = queue;
        _scopeFactory = scopeFactory;
        _serialService = serialService;
        _store = store;
        _printerState = printerState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JobProcessor started.");
        
        // Ensure connection logic is handled. 
        // Depending on design, we might want to connect only when printing, 
        // or stay connected. Let's try to connect effectively once or when needed.
        
        // For this implementation, we'll try to connect at startup loop.
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_serialService.IsConnected)
                {
                    await _serialService.ConnectAsync(stoppingToken); // Try connecting
                }
            }
            catch (Exception ex)
            {
                 _logger.LogError(ex, "Connection failure. Retrying in 10s...");
                 await Task.Delay(10000, stoppingToken);
                 continue;
            }

            var nextId = _queue.TryDequeue();
            if (nextId is null)
            {
                await Task.Delay(1000, stoppingToken);
                continue;
            }

            await ProcessJobAsync(nextId.Value, stoppingToken);
        }
    }

    private async Task ProcessJobAsync(Guid jobId, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Processing Job {JobId}...", jobId);

        // We need a scope to resolve dependencies if they were scoped (Store is Singleton here, so ok, but good practice)
        // Actually Store is Singleton in Program.cs.
        
        try
        {
            _printerState.IsPrinting = true;
            _printerState.CurrentJobId = jobId;
            _printerState.ProcessedLines = 0;
            
            // Update status to Printing
            var job = await _store.UpdateStatusAsync(jobId, PrintJobStatus.Printing);
            if (job is null)
            {
                _logger.LogWarning("Job {JobId} not found in store.", jobId);
                return;
            }

            // Count lines for progress
            // Note: This iterates the string once. For huge files, this might take a moment.
            // Using a specific line counting method or pre-calculating it during upload would be better for massive files.
            // For now, this is acceptable for standard prints.
            using (var lineCounter = new StringReader(job.Content))
            {
                int count = 0;
                while (await lineCounter.ReadLineAsync(stoppingToken) != null) count++;
                _printerState.TotalLines = count;
            }
            
            using var reader = new StringReader(job.Content);
            string? line;
            int lineNumber = 0;
            
            while ((line = await reader.ReadLineAsync(stoppingToken)) != null)
            {
                if (stoppingToken.IsCancellationRequested) return;

                // Check for cancellation status update from API?
                var currentJobState = await _store.GetAsync(jobId);
                if (currentJobState?.Status == PrintJobStatus.Canceled)
                {
                    _logger.LogInformation("Job {JobId} was canceled.", jobId);
                    return; 
                }

                lineNumber++;
                _printerState.ProcessedLines = lineNumber; // Update progress
                
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith(';')) continue;

                await _serialService.SendGCodeLineAsync(line, stoppingToken);
            }

            // Completed
            await _store.UpdateStatusAsync(jobId, PrintJobStatus.Completed);
            _logger.LogInformation("Job {JobId} completed.", jobId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Job {JobId}.", jobId);
            await _store.UpdateStatusAsync(jobId, PrintJobStatus.Failed);
        }
        finally
        {
            _printerState.IsPrinting = false;
            _printerState.CurrentJobId = null;
            _printerState.TotalLines = 0;
            _printerState.ProcessedLines = 0;
        }
    }
}
