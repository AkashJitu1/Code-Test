using System.Text.Json;
using Microsoft.Extensions.Logging;
using SKFProductAssistant.Models;
using StackExchange.Redis;

namespace SKFProductAssistant.Services;

public sealed class RedisFeedbackStore : IFeedbackStore
{
    private const string ListKey = "skf:feedback";

    private readonly IDatabase _db;
    private readonly ILogger<RedisFeedbackStore> _logger;

    public RedisFeedbackStore(IConnectionMultiplexer redis, ILogger<RedisFeedbackStore> logger)
    {
        _db = redis.GetDatabase();
        _logger = logger;
    }

    public async Task StoreAsync(FeedbackEntry entry, CancellationToken ct = default)
    {
        try
        {
            await _db.ListRightPushAsync(ListKey, JsonSerializer.Serialize(entry));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save feedback to Redis for session {SessionId}", entry.SessionId);
        }
    }
}
