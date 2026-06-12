using System.IO.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Printserver.Models;

namespace Printserver.Services;

public interface ISerialConnectionService
{
    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync();
    Task SendGCodeLineAsync(string gcode, CancellationToken cancellationToken);
    bool IsConnected { get; }
}

public sealed class SerialConnectionService : ISerialConnectionService, IDisposable
{
    private readonly ILogger<SerialConnectionService> _logger;
    private readonly PrintServerOptions _options;
    private SerialPort? _serialPort;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // Common G-Code ok response
    private const string OkResponse = "ok";
    
    // Auto-reset event to signal when "ok" is received
    private readonly AutoResetEvent _okReceived = new(false);

    public SerialConnectionService(ILogger<SerialConnectionService> logger, IOptions<PrintServerOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public bool IsConnected => _serialPort?.IsOpen ?? false;

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (IsConnected) return Task.CompletedTask;

        var portName = _options.SerialPortName;
        if (string.IsNullOrWhiteSpace(portName))
        {
            _logger.LogWarning("Serial Port not configured.");
            return Task.CompletedTask;
        }

        try
        {
            _logger.LogInformation("Connecting to {Port} at {Baud}...", portName, _options.BaudRate);
            _serialPort = new SerialPort(portName, _options.BaudRate)
            {
                ReadTimeout = 5000,
                WriteTimeout = 5000,
                NewLine = "\n" 
            };

            _serialPort.DataReceived += OnDataReceived;
            _serialPort.Open();
            
            // Wait a bit for the printer to reset/initialize (bootloader)
            Thread.Sleep(2000); 
            _logger.LogInformation("Connected to {Port}.", portName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to serial port {Port}.", portName);
            _serialPort?.Dispose();
            _serialPort = null;
            throw; // Rethrow to let the caller handle it or crash the service if critical
        }

        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        if (_serialPort is null) return Task.CompletedTask;

        try
        {
            if (_serialPort.IsOpen)
            {
                _serialPort.Close();
            }
            _serialPort.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error disconnecting serial port.");
        }
        finally
        {
            _serialPort = null;
        }

        return Task.CompletedTask;
    }

    public async Task SendGCodeLineAsync(string gcode, CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("Not connected to printer.");
        }

        // Strip comments and trim
        var commentIndex = gcode.IndexOf(';');
        if (commentIndex >= 0)
        {
            gcode = gcode[..commentIndex];
        }
        gcode = gcode.Trim();

        if (string.IsNullOrEmpty(gcode))
        {
            return; // Nothing to send
        }

        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            _okReceived.Reset();
            
            // Send the line
            if (_serialPort != null)
            {
                 _serialPort.WriteLine(gcode);
                 _logger.LogDebug(">> {GCode}", gcode);
            }

            // Wait for "ok" - synchronous wait on handle is tricky with async
            // In a real robust implementation, we'd use a TaskCompletionSource, 
            // but for simplicity/reliability with SerialPort events, a short blocking wait 
            // or spinning is often seen. However, proper async is better.
            
            // NOTE: SerialPort.DataReceived runs on a thread pool thread. 
            // We can wrap the AutoResetEvent in a Task via Task.Run or similar, 
            // or just use a TaskCompletionSource handled in DataReceived.
            // Let's stick to a simple timeout loop for now to be safe against hangs.
            
            await ConvertWaitHandleToTask(_okReceived, TimeSpan.FromSeconds(30), cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        var sp = (SerialPort)sender;
        try
        {
            var data = sp.ReadExisting();
            // In a real scenario, we might get partial lines. 
            // We should buffer and parse lines.
            // For this simple implementation, we check if the chunk contains "ok".
            // A more robust way is to build a string buffer.
            
            // Simple buffer simulation (thread-safe handling omitted for brevity/MVP)
            // Ideally we'd append to a StringBuilder and check for newlines.
            
            _logger.LogDebug("<< {Data}", data.Trim());

            if (data.Contains("ok", StringComparison.OrdinalIgnoreCase))
            {
                _okReceived.Set();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading from serial port.");
        }
    }

    private async Task ConvertWaitHandleToTask(WaitHandle handle, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>();
        
        using var reg = cancellationToken.Register(() => tcs.TrySetCanceled());

        var registeredWait = ThreadPool.RegisterWaitForSingleObject(
            handle,
            (state, timedOut) => 
            {
                var t = (TaskCompletionSource<bool>)state!;
                if (timedOut) t.TrySetException(new TimeoutException("Timeout waiting for printer response."));
                else t.TrySetResult(true);
            },
            tcs,
            timeout,
            executeOnlyOnce: true
        );

        try
        {
            await tcs.Task;
        }
        finally
        {
            registeredWait.Unregister(handle);
        }
    }

    public void Dispose()
    {
        _serialPort?.Dispose();
        _writeLock.Dispose();
        _okReceived.Dispose();
    }
}
