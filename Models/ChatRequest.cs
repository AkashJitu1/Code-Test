using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace SKFProductAssistant.Models;

public sealed class ChatRequest
{
    [JsonPropertyName("message")]
    [Required]
    public string Message { get; init; } = string.Empty;

    // Pass back the sessionId from a previous response to continue the conversation.
    // Omit to start a new session.
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}
