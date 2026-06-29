using Microsoft.SemanticKernel;
using SKFProductAssistant.Models;

namespace SKFProductAssistant.Agents;

public interface IAgent
{
    Task<string> HandleAsync(
        Kernel kernel,
        string userMessage,
        string sessionId,
        ConversationState state,
        CancellationToken ct = default);
}
