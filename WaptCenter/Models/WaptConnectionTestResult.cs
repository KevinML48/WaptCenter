using System.Net;

namespace WaptCenter.Models;

public sealed class WaptConnectionTestResult
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? TechnicalDetails { get; set; }

    public HttpStatusCode? StatusCode { get; set; }
}