namespace SKFProductAssistant.Services;

public interface ICacheService
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);
    Task SetAsync(string key, string value, TimeSpan? ttl = null, CancellationToken ct = default);
}
