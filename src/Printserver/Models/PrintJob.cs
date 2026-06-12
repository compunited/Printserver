
using System.Text.Json.Serialization;

namespace Printserver.Models;

public sealed record PrintJob(
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

public sealed record PrintJobRequest(string Name, string Content);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrintJobStatus
{
    Queued,
    Printing,
    Completed,
    Canceled,
    Failed
}
