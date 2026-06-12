namespace Printserver.Services;

public interface IPrinterStateService
{
    bool IsPrinting { get; set; }
    int TotalLines { get; set; }
    int ProcessedLines { get; set; }
    Guid? CurrentJobId { get; set; }
    double Progress { get; }
}

public class PrinterStateService : IPrinterStateService
{
    public bool IsPrinting { get; set; }
    public int TotalLines { get; set; }
    public int ProcessedLines { get; set; }
    public Guid? CurrentJobId { get; set; }
    
    public double Progress => TotalLines > 0 ? Math.Round((double)ProcessedLines / TotalLines * 100, 2) : 0;
}
