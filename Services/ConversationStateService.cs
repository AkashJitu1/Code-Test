using System.Collections.Concurrent;
using SKFProductAssistant.Models;

namespace SKFProductAssistant.Services;

// In-memory store keyed by sessionId. Not shared across Function host instances —
// swap this for a Redis-backed implementation for multi-instance deployments.
public sealed class ConversationStateService : IConversationStateService
{
    private readonly ConcurrentDictionary<string, ConversationState> _sessions = new();

    public ConversationState GetOrCreate(string sessionId) =>
        _sessions.GetOrAdd(sessionId, _ => new ConversationState());

    public void Update(string sessionId, ConversationState state)
    {
        state.LastUpdated = DateTime.UtcNow;
        _sessions[sessionId] = state;
    }
}
