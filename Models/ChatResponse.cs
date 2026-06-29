using System.Text.Json.Serialization;

namespace SKFProductAssistant.Models;

public sealed class ChatResponse
{
    [JsonPropertyName("answer")]
    public string Answer { get; init; } = string.Empty;

    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = string.Empty;

    [JsonPropertyName("intent")]
    public string Intent { get; init; } = string.Empty;
}
