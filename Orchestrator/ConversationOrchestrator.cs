using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using SKFProductAssistant.Agents;
using SKFProductAssistant.Models;
using SKFProductAssistant.Services;

namespace SKFProductAssistant.Orchestrator;

public sealed class ConversationOrchestrator(
    QAAgent qaAgent,
    FeedbackAgent feedbackAgent,
    IConversationStateService stateService,
    IServiceProvider services,
    ILogger<ConversationOrchestrator> logger)
{
    // Longest first so "6205 N" doesn't accidentally match as plain "6205"
    private static readonly string[] KnownDesignations = ["6205 N", "6205N", "6205"];

    private static readonly Dictionary<string, string> AttributeHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["width"]     = "width",
        ["bore"]      = "bore diameter",
        ["diameter"]  = "diameter",
        ["outside"]   = "outside diameter",
        ["height"]    = "height",
        ["weight"]    = "product net weight",
        ["speed"]     = "limiting speed",
        ["load"]      = "basic dynamic load rating",
        ["dynamic"]   = "basic dynamic load rating",
        ["static"]    = "basic static load rating",
        ["tolerance"] = "tolerance class",
        ["cage"]      = "cage",
        ["sealing"]   = "sealing",
        ["material"]  = "material, bearing",
        ["lubricant"] = "lubricant"
    };

    // Checked before hitting the model — saves a round-trip for obvious feedback messages
    private static readonly string[] FeedbackKeywords =
        ["wrong", "incorrect", "correction", "store my", "save my", "feedback", "not right"];

    private const string ClassificationPrompt = """
        Classify the user message as EXACTLY one of two intents:
        - "question"  : the user is asking about a product specification, attribute, or description.
        - "feedback"  : the user is providing a correction, rating, complaint, or note about a previous answer.

        User message: "{message}"

        Respond with exactly one word — "question" or "feedback" — and nothing else.
        """;

    public async Task<(string answer, string intent)> ProcessAsync(
        string userMessage,
        string sessionId,
        CancellationToken ct = default)
    {
        var state  = stateService.GetOrCreate(sessionId);
        var kernel = services.GetRequiredService<Kernel>();
        var intent = await ClassifyIntentAsync(kernel, userMessage, state, ct);

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Session {SessionId}: intent={Intent}", sessionId, intent);

        var answer = intent == "feedback"
            ? await feedbackAgent.HandleAsync(kernel, userMessage, sessionId, state, ct)
            : await qaAgent.HandleAsync(kernel, userMessage, sessionId, state, ct);

        if (intent == "question")
            UpdateQuestionContext(state, userMessage, answer);

        state.History.Add(new ChatMessage("user", userMessage));
        state.History.Add(new ChatMessage("assistant", answer));
        state.LastTurnWasQuestion = intent == "question";
        stateService.Update(sessionId, state);

        return (answer, intent);
    }

    private async Task<string> ClassifyIntentAsync(
        Kernel kernel,
        string userMessage,
        ConversationState state,
        CancellationToken ct)
    {
        var lower = userMessage.ToLowerInvariant();

        if (FeedbackKeywords.Any(lower.Contains) ||
            (lower.Contains("actually") && state.LastTurnWasQuestion))
            return "feedback";

        try
        {
            var history = new ChatHistory();
            history.AddUserMessage(ClassificationPrompt.Replace("{message}", userMessage.Replace("\"", "'")));

            var result = await kernel
                .GetRequiredService<IChatCompletionService>()
                .GetChatMessageContentAsync(history, new AzureOpenAIPromptExecutionSettings(), kernel, ct);

            var raw = (result.Content ?? "question").Trim().ToLowerInvariant();
            return raw.StartsWith("feedback") ? "feedback" : "question";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Intent classification failed; defaulting to 'question'");
            return "question";
        }
    }

    // Pulls product name and attribute from the message using simple keyword matching.
    // Stored in state so follow-up questions like "And its bore diameter?" resolve correctly.
    private static void UpdateQuestionContext(ConversationState state, string userMessage, string answer)
    {
        var designation = KnownDesignations
            .FirstOrDefault(d => userMessage.Contains(d, StringComparison.OrdinalIgnoreCase));

        if (designation is not null)
            state.LastDesignation = designation;

        var attribute = AttributeHints
            .FirstOrDefault(kv => userMessage.Contains(kv.Key, StringComparison.OrdinalIgnoreCase));

        if (attribute.Key is not null)
            state.LastAttribute = attribute.Value;

        state.LastAnswer = answer;
    }
}
