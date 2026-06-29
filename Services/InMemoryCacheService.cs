using System.Collections.Concurrent;

namespace SKFProductAssistant.Services;

public sealed class InMemoryCacheService : ICacheService
{
    private sealed record Entry(string Value, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _store = new();

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        if (_store.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
            return Task.FromResult<string?>(entry.Value);

        _store.TryRemove(key, out _);
        return Task.FromResult<string?>(null);
    }

    public Task SetAsync(string key, string value, TimeSpan? ttl = null, CancellationToken ct = default)
    {
        _store[key] = new Entry(value, DateTime.UtcNow.Add(ttl ?? TimeSpan.FromHours(1)));
        return Task.CompletedTask;
    }
}
