using System.ComponentModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using SKFProductAssistant.Services;

namespace SKFProductAssistant.Plugins;

public sealed class CachePlugin
{
    private readonly ICacheService _cache;
    private readonly TimeSpan _ttl;
    private readonly ILogger<CachePlugin> _logger;

    public CachePlugin(ICacheService cache, IConfiguration config, ILogger<CachePlugin> logger)
    {
        _cache = cache;
        _logger = logger;
        var ttlSeconds = int.TryParse(config["CACHE_TTL_SECONDS"], out var s) ? s : 3600;
        _ttl = TimeSpan.FromSeconds(ttlSeconds);
    }

    [KernelFunction("GetCachedAnswer")]
    [Description("Retrieve a previously cached answer for a product/attribute combination. " +
                 "Returns the cached answer string, or null if not found.")]
    public async Task<string?> GetCachedAnswerAsync(
        [Description("Cache key in the format '{designation}:{attribute}' lowercase, e.g. '6205:width'")]
        string cacheKey)
    {
        var result = await _cache.GetAsync(cacheKey.ToLowerInvariant());
        if (result is not null)
            _logger.LogInformation("Cache HIT for key {Key}", cacheKey);
        return result;
    }

    [KernelFunction("SetCachedAnswer")]
    [Description("Store an answer in the cache for future lookups.")]
    public async Task SetCachedAnswerAsync(
        [Description("Cache key in the format '{designation}:{attribute}' lowercase")]
        string cacheKey,
        [Description("The answer text to cache")]
        string answer)
    {
        await _cache.SetAsync(cacheKey.ToLowerInvariant(), answer, _ttl);
        _logger.LogInformation("Cache SET for key {Key}", cacheKey);
    }
}
