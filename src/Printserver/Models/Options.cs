namespace Printserver.Models;

public sealed class PrintServerOptions
{
    public string? SerialPortName { get; set; }
    public int BaudRate { get; set; } = 115200;
    public string DataDirectory { get; set; } = "print_jobs";
    
    public List<PreheatProfile> PreheatProfiles { get; set; } = new();
}

public sealed record PreheatProfile(string Name, int ExtruderTemp, int BedTemp);

public sealed class WebcamOptions
{
    public string? SnapshotPath { get; init; }
    public string? MjpegUrl { get; init; }
}
