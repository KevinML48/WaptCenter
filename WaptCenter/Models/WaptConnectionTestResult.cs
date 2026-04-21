using System.Net;

namespace WaptCenter.Models;

public sealed class WaptConnectionTestResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;

    public string? TechnicalDetails { get; init; }

    public HttpStatusCode? StatusCode { get; init; }
}
