namespace WaptCenter.Models;

public sealed class WaptConnectionTestResult
{
    public bool Success { get; init; }

    public string Message { get; init; } = string.Empty;
}
