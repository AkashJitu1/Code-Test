namespace SKFProductAssistant.Models;

public sealed class FeedbackEntry
{
    public string SessionId { get; init; } = string.Empty;
    public string? Designation { get; init; }
    public string? Attribute { get; init; }
    public string FeedbackText { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
