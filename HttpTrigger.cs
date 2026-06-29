using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SKFProductAssistant.Models;
using SKFProductAssistant.Orchestrator;

namespace SKFProductAssistant;

public sealed class HttpTrigger
{
    private readonly ConversationOrchestrator _orchestrator;
    private readonly ILogger<HttpTrigger> _logger;
    private readonly int _maxMessageLength;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public HttpTrigger(ConversationOrchestrator orchestrator, IConfiguration config, ILogger<HttpTrigger> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
        _maxMessageLength = int.TryParse(config["MAX_MESSAGE_LENGTH"], out var m) ? m : 2000;
    }

    [Function("Chat")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "chat")]
        HttpRequestData req,
        CancellationToken ct)
    {
        ChatRequest chatRequest;
        try
        {
            var body = await req.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body))
                return await ErrorResponse(req, HttpStatusCode.BadRequest, "Request body is empty.");

            chatRequest = JsonSerializer.Deserialize<ChatRequest>(body, JsonOptions)
                ?? throw new JsonException("Null deserialization result");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Invalid JSON in request body");
            return await ErrorResponse(req, HttpStatusCode.BadRequest, "Invalid JSON in request body.");
        }

        if (string.IsNullOrWhiteSpace(chatRequest.Message))
            return await ErrorResponse(req, HttpStatusCode.BadRequest, "Field 'message' is required and cannot be blank.");

        if (chatRequest.Message.Length > _maxMessageLength)
            return await ErrorResponse(req, HttpStatusCode.BadRequest,
                $"Message exceeds the maximum allowed length of {_maxMessageLength} characters.");

        var sessionId = ResolveSessionId(req, chatRequest.SessionId);
        _logger.LogInformation("Request received. Session={SessionId}", sessionId);

        try
        {
            var (answer, intent) = await _orchestrator.ProcessAsync(chatRequest.Message, sessionId, ct);

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");
            response.Headers.Add("X-Session-Id", sessionId);

            await response.WriteStringAsync(JsonSerializer.Serialize(
                new ChatResponse { Answer = answer, SessionId = sessionId, Intent = intent },
                JsonOptions), ct);

            return response;
        }
        catch (OperationCanceledException)
        {
            return await ErrorResponse(req, HttpStatusCode.ServiceUnavailable, "Request was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error for session {SessionId}", sessionId);
            return await ErrorResponse(req, HttpStatusCode.InternalServerError,
                "Something went wrong on our end. Please try again.");
        }
    }

    // Priority: body field → X-Session-Id header → generate new GUID
    // Raw value is sanitised to [A-Za-z0-9_-] so we never store user input as a dictionary key
    private static string ResolveSessionId(HttpRequestData req, string? fromBody)
    {
        if (!string.IsNullOrWhiteSpace(fromBody))
            return Sanitise(fromBody);

        if (req.Headers.TryGetValues("X-Session-Id", out var values))
        {
            var fromHeader = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(fromHeader))
                return Sanitise(fromHeader);
        }

        return Guid.NewGuid().ToString("N");
    }

    private static string Sanitise(string raw) =>
        new string(raw.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(64).ToArray());

    private static async Task<HttpResponseData> ErrorResponse(HttpRequestData req, HttpStatusCode status, string message)
    {
        var response = req.CreateResponse(status);
        response.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await response.WriteStringAsync(JsonSerializer.Serialize(new { error = message }, JsonOptions));
        return response;
    }
}
