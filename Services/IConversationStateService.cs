using SKFProductAssistant.Models;

namespace SKFProductAssistant.Services;

public interface IConversationStateService
{
    ConversationState GetOrCreate(string sessionId);
    void Update(string sessionId, ConversationState state);
}
