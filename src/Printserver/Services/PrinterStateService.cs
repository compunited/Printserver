namespace Printserver.Services;

public interface IPrinterStateService
{
    bool IsPrinting { get; set; }
}

public class PrinterStateService : IPrinterStateService
{
    public bool IsPrinting { get; set; }
}
