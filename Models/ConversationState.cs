namespace SKFProductAssistant.Models;

public sealed class ConversationState
{
    public List<ChatMessage> History { get; init; } = new();
    public string? LastDesignation { get; set; }
    public string? LastAttribute { get; set; }
    public string? LastAnswer { get; set; }
    // Helps the orchestrator recognise follow-on feedback like "that was wrong"
    public bool LastTurnWasQuestion { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public sealed record ChatMessage(string Role, string Content);
