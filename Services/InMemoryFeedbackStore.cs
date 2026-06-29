using System.Collections.Concurrent;
using SKFProductAssistant.Models;

namespace SKFProductAssistant.Services;

public sealed class InMemoryFeedbackStore : IFeedbackStore
{
    private readonly ConcurrentBag<FeedbackEntry> _entries = new();

    public Task StoreAsync(FeedbackEntry entry, CancellationToken ct = default)
    {
        _entries.Add(entry);
        return Task.CompletedTask;
    }
}
