# AI-Assisted Code Validation Report
**Project:** SKF Product Assistant (Mini)
**Stack:** C# / .NET 10 / Azure Functions v4 / Microsoft Semantic Kernel 1.77 / Azure OpenAI
**Review scope:** Full solution — architecture, code quality, performance, security, reliability

---

## 1. Executive Summary

The solution correctly implements the three-component pattern (Orchestrator → Agent → Plugin) described in the assignment. It separates concerns cleanly, uses dependency inversion throughout, and includes meaningful hallucination controls. The main improvements applied during review were:

- AI-powered attribute extraction (replaced hardcoded C# keyword scoring)
- Static readonly data structures instead of per-call allocations
- Primary constructor pattern to remove field-assignment boilerplate
- `IsEnabled` guard on hot-path log calls
- `GetRequiredService<T>()` replacing unsafe manual casts

Remaining items to watch are noted in each section below.

---

## 2. Architecture Review

### ✅ Strengths

**Separation of concerns is clear and enforced.**
Each class has one job. `DatasheetPlugin` reads files. `CachePlugin` wraps the cache service. `QAAgent` orchestrates AI calls. Nothing leaks across boundaries.

**Dependency inversion is consistent.**
All storage is behind `ICacheService`, `IFeedbackStore`, and `IConversationStateService`. Swapping Redis in or out is a single config flag change — no business logic is touched.

**Function-scoped tool access.**
`FunctionChoiceBehavior.Auto(functions: ...)` limits each agent to only its relevant plugins. This prevents cross-agent tool calls and reduces prompt confusion. It is a deliberate and correct architectural choice.

**Transient Kernel per request.**
Creating a new `Kernel` instance per request avoids concurrency issues that would arise from sharing a single Kernel across requests. The plugins themselves are singletons, which is correct since they are stateless after construction.

### ⚠️ Observations

**Conversation state is not shared across Function host instances.**
`ConversationStateService` stores state in a `ConcurrentDictionary` in process memory. This works for single-instance local development but will silently break in a multi-instance Azure deployment — different instances will have different views of the same session. For production, this service should be backed by Redis or Azure Cosmos DB.

**`IServiceProvider` in the Orchestrator is a service-locator pattern.**
`ConversationOrchestrator` calls `services.GetRequiredService<Kernel>()` to get a transient Kernel. This is done because registering `Kernel` as transient in the host DI and injecting it into a singleton orchestrator would create a captive-dependency issue. The current approach is pragmatic but worth documenting. An alternative is a `IKernelFactory` abstraction.

**No retry/resilience policy on Azure OpenAI calls.**
All Azure OpenAI calls are single-attempt. Transient HTTP 429 (rate limit) and 503 errors will propagate as exceptions and return error messages to users. Consider wrapping with Polly or the SK built-in retry middleware for production.

---

## 3. Code Quality

### ✅ Strengths

- **KISS applied throughout.** Static readonly fields for `KnownDesignations`, `AttributeHints`, and `FeedbackKeywords` are built once at startup instead of per-call.
- **Primary constructor** in `ConversationOrchestrator` eliminates boilerplate field-assignment code.
- **`file`-scoped / `internal sealed`** access modifiers keep deserialization model classes from leaking out of `DatasheetPlugin.cs`.
- **XML documentation** on every public class and method with plain-language descriptions.
- **No magic strings** for agent names, cache key patterns, or plugin names — constants or readonly arrays are used.

### ⚠️ Observations

**`BuildSystemPrompt` allocates a `List<string>` on every feedback turn.**
`FeedbackAgent.BuildSystemPrompt` creates a new `List<string>` to build the context block. Given the small size (≤3 items), this is minor, but a `StringBuilder` or string interpolation would be more direct.

**`GetQaFunctions` uses `Contains` on `KernelPluginCollection`.**
`kernel.Plugins.Contains(name)` requires verifying the API exists on `KernelPluginCollection`. If it does not, the LINQ chain will silently skip plugins. Consider using `TryGetPlugin` with a null check for explicit error handling.

**`DatasheetPlugin.LookupAttributeAsync` makes a synchronous `string.Replace` call to strip markdown.**
The `TrimStart("json")` call after stripping backticks trims individual characters, not the substring `"json"`. Use `response.TrimStart('`', 'j', 's', 'o', 'n')` is also wrong — use `response.StartsWith("```json")` with explicit handling instead:
```csharp
if (response.StartsWith("```"))
{
    response = response.Trim('`');
    if (response.StartsWith("json"))
        response = response[4..].TrimStart();
}
```

**`AttributeLookupResult.cs` is now a comment file.**
After the AI extraction refactor, the model classes in `AttributeLookupResult.cs` are no longer used in code — only as a comment contract. Either delete the file or restore the class definitions for use as a typed deserialization target in tests.

---

## 4. Performance Considerations

| Area | Observation | Recommendation |
|------|-------------|----------------|
| **AI extraction per lookup** | `LookupAttribute` now makes a nested AI call inside the outer agent call (one call for extraction, one for the answer). This is one extra round-trip compared to the old C# matching. | The cache mitigates this for repeated questions. For high-traffic deployments, consider a short-circuit: if the attribute is unambiguous from a quick key check, return it directly without the inner AI call. |
| **Full JSON in prompt** | The product JSON (~200 lines) is included in the extraction prompt on every uncached call. At ~2 KB per product, this is within normal prompt budgets but costs tokens. | Acceptable for this dataset size. For larger datasheets, consider passing only the relevant section (dimensions / performance / etc.) based on a coarse category guess. |
| **In-memory cache** | The default `InMemoryCacheService` uses `DateTime.UtcNow` comparison on every read — cheap but not zero-cost in tight loops. | Fine for this use case. If throughput becomes a concern, consider `System.Runtime.Caching.MemoryCache` which has built-in eviction policies. |
| **`state.History.TakeLast(N)`** | Called on every Q&A turn. For sessions with many turns, this iterates the whole list each time. | Use a bounded `Queue<ChatMessage>` capped at `MAX_HISTORY_TURNS * 2` to avoid the scan. |
| **`KnownDesignations` search** | Linear scan across 3 items — negligible. | No action needed at this scale; fine to extend the list. |

---

## 5. Security

### ✅ Applied controls

- **No hardcoded secrets.** All credentials are read from environment variables / `local.settings.json`.
- **Input validation at the boundary.** Message length is capped; blank messages are rejected; session IDs are sanitised to `[A-Za-z0-9_-]` (max 64 chars).
- **Prompt injection mitigation.** Both agent system prompts include an explicit rule telling the model to ignore user instructions that attempt to override its behaviour.
- **Error messages do not expose internals.** Redis errors, AI call failures, and unexpected exceptions all return generic user-facing messages while logging details server-side.
- **`local.settings.json` is excluded from publish** (`<CopyToPublishDirectory>Never</CopyToPublishDirectory>`).

### ⚠️ Observations

**Session ID is not validated as a GUID.**
The sanitised session ID can be any alphanumeric string up to 64 chars. A malicious client can choose predictable IDs (e.g. `admin`, `user1`) and potentially read another user's conversation state if they know the ID. For production, generate the session ID server-side on first request and do not allow client-supplied IDs to refer to existing sessions unless they were issued by the server.

**`AuthorizationLevel.Function` requires a function key in production.**
The trigger uses `AuthorizationLevel.Function`, which requires an `?code=` query parameter or `x-functions-key` header. This is correct for Azure but worth confirming that the key is rotated and not exposed in URLs stored in logs.

**Product JSON is loaded from disk at startup without integrity verification.**
If the `Data/` folder is writable, an attacker who can write files could inject malicious JSON that is then passed verbatim to Azure OpenAI. For production, consider verifying a checksum or restricting file-system permissions on the Data folder.

**The extraction prompt includes the full product JSON as user-controlled content in an inner AI call.**
While the product JSON is loaded from local files (not user input), it is worth noting that the inner extraction call does not use a system/user message split — the product data is in the user message. Consider placing the JSON in a `system` message role in the extraction call to give it higher trust in the model's eyes.

---

## 6. Reliability

### ✅ Strengths

- Redis errors are caught and logged as warnings, not propagated — a cache miss is always safe.
- `DatasheetPlugin.LoadAll` skips bad files individually rather than crashing on the first error.
- `ClassifyIntentAsync` defaults to "question" on any failure — the user still gets a response.
- All agent `HandleAsync` methods have top-level `try/catch` that return friendly error messages.

### ⚠️ Observations

**No timeout on individual Azure OpenAI calls.**
Long-running or stalled model calls will hold the Azure Functions execution thread until the default HTTP client timeout (usually 100 seconds). Pass a `CancellationToken` with a shorter deadline (e.g. 30 seconds) to the inner extraction call in `DatasheetPlugin`.

**`DatasheetPlugin` is a singleton but holds mutable `Dictionary<string, string>`.**
The dictionary is written only during construction (single-threaded) and read-only thereafter, so this is safe. However, if hot-reload or live datasheet updates are added in the future, thread safety must be revisited.

**No health check endpoint.**
There is no `/api/health` endpoint to verify that the function host, Redis connection, and Azure OpenAI connectivity are all working. This makes it harder to diagnose issues in a deployed environment.

---

## 7. Recommended Improvements

### High priority

1. **Back conversation state with Redis** for multi-instance deployments.
   ```csharp
   // Add IConversationStateService implementation that serializes/deserializes
   // ConversationState to/from a Redis hash keyed by sessionId
   ```

2. **Fix the markdown strip logic in `DatasheetPlugin`.**
   ```csharp
   if (response.StartsWith("```"))
   {
       response = response.Trim('`');
       if (response.StartsWith("json"))
           response = response[4..].TrimStart();
   }
   ```

3. **Add a cancellation deadline to the inner AI extraction call** in `LookupAttributeAsync` to prevent unbounded waits.

### Medium priority

4. **Add Polly retry with exponential back-off** on Azure OpenAI calls for 429/503 resilience.

5. **Server-side session ID generation** — do not allow clients to supply arbitrary session IDs; issue them on first contact.

6. **Replace `AttributeLookupResult.cs` comments with actual typed classes** and deserialize the AI's JSON response into them for type safety in future tests.

### Low priority

7. **Replace `state.History.TakeLast(N)`** with a bounded queue to avoid O(n) scans on long sessions.

8. **Add a `/api/health` Azure Function** that checks Redis connectivity and Azure OpenAI reachability.

9. **Move product JSON into a system message** in the inner extraction call for a stronger trust boundary.

---

## 8. Summary Scorecard

| Dimension | Rating | Notes |
|-----------|--------|-------|
| Architecture clarity | ✅ Good | Clear separation; minor service-locator smell in orchestrator |
| Code quality | ✅ Good | KISS applied; one markdown-strip bug to fix |
| Performance | ⚠️ Adequate | Extra AI round-trip per uncached lookup; mitigated by cache |
| Security | ⚠️ Adequate | Good baseline; session ID and JSON integrity need hardening for production |
| Reliability | ⚠️ Adequate | No retry policy; no timeout on inner AI call; no health check |
| Testability | ✅ Good | Interfaces everywhere; `Kernel` injected as method parameter |
| Hallucination control | ✅ Strong | Tool gating + extraction prompt + `found: false` handling |
