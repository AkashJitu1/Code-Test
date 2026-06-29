using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using SKFProductAssistant.Models;
using SKFProductAssistant.Services;

namespace SKFProductAssistant.Plugins;

public sealed class FeedbackPlugin
{
    private readonly IFeedbackStore _store;
    private readonly ILogger<FeedbackPlugin> _logger;

    public FeedbackPlugin(IFeedbackStore store, ILogger<FeedbackPlugin> logger)
    {
        _store = store;
        _logger = logger;
    }

    [KernelFunction("StoreFeedback")]
    [Description("Persist user feedback about a product attribute answer. " +
                 "Call this whenever the user provides a correction, rates the answer, or leaves a note.")]
    public async Task<string> StoreFeedbackAsync(
        [Description("The session identifier for this conversation")]
        string sessionId,
        [Description("Product designation the feedback refers to (e.g. '6205'), or empty if unknown")]
        string designation,
        [Description("Product attribute the feedback refers to (e.g. 'width'), or empty if unknown")]
        string attribute,
        [Description("Verbatim or summarised feedback text from the user")]
        string feedbackText)
    {
        var entry = new FeedbackEntry
        {
            SessionId    = sessionId,
            Designation  = string.IsNullOrWhiteSpace(designation) ? null : designation.Trim(),
            Attribute    = string.IsNullOrWhiteSpace(attribute)   ? null : attribute.Trim(),
            FeedbackText = feedbackText.Trim(),
            Timestamp    = DateTime.UtcNow
        };

        await _store.StoreAsync(entry);
        _logger.LogInformation("Feedback saved — session {SessionId}, {Designation}/{Attribute}",
            sessionId, designation, attribute);

        var label = (!string.IsNullOrWhiteSpace(designation) && !string.IsNullOrWhiteSpace(attribute))
            ? $"{designation} / {attribute}"
            : "your recent interaction";

        return $"stored:true|label:{label}";
    }
}
