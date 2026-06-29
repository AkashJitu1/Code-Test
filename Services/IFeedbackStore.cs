using SKFProductAssistant.Models;

namespace SKFProductAssistant.Services;

public interface IFeedbackStore
{
    Task StoreAsync(FeedbackEntry entry, CancellationToken ct = default);
}
