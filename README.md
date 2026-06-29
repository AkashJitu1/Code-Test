# SKF Product Assistant (Mini)

A conversational HTTP endpoint that answers natural-language questions about SKF bearing datasheets. It uses **Microsoft Semantic Kernel**, **Azure OpenAI**, and **Azure Functions (.NET 10)** to let users ask things like "What is the width of 6205?" and get back the exact value from the local JSON files — with no guessing.

---

## High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        POST /api/chat                           │
│                        HttpTrigger                              │
│  • Input validation (length, blank check, session ID)          │
│  • Session ID resolution (body → header → generate)            │
└───────────────────────────┬─────────────────────────────────────┘
                            │
                            ▼
┌─────────────────────────────────────────────────────────────────┐
│                   ConversationOrchestrator                      │
│  • Loads / creates ConversationState for the session            │
│  • Fast-path keyword classification (no model call)             │
│  • Falls back to Azure OpenAI one-word intent classification    │
│  • Routes to QAAgent or FeedbackAgent                          │
│  • Saves updated state after the turn                           │
└───────────────┬─────────────────────────┬───────────────────────┘
                │                         │
                ▼                         ▼
┌──────────────────────┐     ┌─────────────────────────┐
│       QAAgent        │     │      FeedbackAgent       │
│                      │     │                          │
│ Tools available:     │     │ Tools available:         │
│  • GetCachedAnswer   │     │  • StoreFeedback         │
│  • LookupAttribute   │     │                          │
│  • GetRawProductData │     │ Uses conversation state  │
│  • SetCachedAnswer   │     │ to resolve "that was     │
│  • GetProductSummary │     │ wrong" references        │
│  • ListAvailable     │     └──────────┬───────────────┘
│    Products          │                │
└──────────┬───────────┘                │
           │                            ▼
           │                  ┌──────────────────────┐
           │                  │    FeedbackPlugin     │
           │                  │  StoreFeedback()      │
           │                  │  → Redis or in-memory │
           │                  └──────────────────────┘
           │
           ▼
┌──────────────────────────────────────────────┐
│             DatasheetPlugin (AI-powered)     │
│                                              │
│  LookupAttribute(designation, attribute)     │
│  • Passes full product JSON + query to AI    │
│  • AI extracts the value — no hardcoded      │
│    keyword matching or aliases               │
│                                              │
│  GetRawProductData(designation)              │
│  • Returns the complete JSON string          │
│                                              │
│  GetProductSummary(designation)              │
│  • Parses description + benefits             │
└──────────────────────────────────────────────┘

┌──────────────────────────────────────────────┐
│                 CachePlugin                  │
│  GetCachedAnswer / SetCachedAnswer           │
│  → RedisCacheService  (USE_REDIS=true)       │
│  → InMemoryCacheService (default)            │
└──────────────────────────────────────────────┘
```

---

## End-to-End Processing Flow

### Question turn (e.g. "What is the width of 6205?")

```
1.  Client  →  POST /api/chat  {"message": "What is the width of 6205?"}

2.  HttpTrigger
    ├── Validates message (not blank, within length limit)
    └── Resolves sessionId (body > X-Session-Id header > new GUID)

3.  ConversationOrchestrator
    ├── Loads ConversationState for the session (or creates a blank one)
    ├── Intent classification
    │   ├── Fast path: message contains "wrong"/"correction"/etc. → "feedback"
    │   └── Slow path: asks Azure OpenAI → "question"
    └── Routes to QAAgent

4.  QAAgent
    ├── Builds ChatHistory (system prompt + last N turns + context note)
    └── Calls Azure OpenAI with FunctionChoiceBehavior.Auto
        │
        ├── Model calls GetCachedAnswer("6205:width")
        │   └── Cache miss → continue
        │
        ├── Model calls LookupAttribute("6205", "width")
        │   ├── DatasheetPlugin reads 6205.json from memory
        │   ├── Builds extraction prompt with full JSON + query
        │   ├── Calls Azure OpenAI (no tools, plain extraction)
        │   └── Returns: {"found": true, "name": "Width", "value": "15", "unit": "mm"}
        │
        ├── Model calls SetCachedAnswer("6205:width", "The width of the 6205 bearing is 15 mm.")
        │
        └── Model returns: "The width of the 6205 bearing is 15 mm."

5.  ConversationOrchestrator
    ├── Appends (user, assistant) messages to ConversationState.History
    ├── Extracts LastDesignation = "6205", LastAttribute = "width"
    └── Saves updated state

6.  HttpTrigger
    └── Returns 200: {"answer": "The width of the 6205 bearing is 15 mm.",
                      "sessionId": "abc123", "intent": "question"}
```

### Follow-up turn (e.g. "And its bore diameter?")

```
1.  Client → POST /api/chat {"message": "And its bore diameter?", "sessionId": "abc123"}

2.  ConversationOrchestrator loads existing state
    └── state.LastDesignation = "6205", state.LastAttribute = "width"

3.  QAAgent injects context note into system prompt:
    "[Context from previous turn] last product: 6205; last attribute: width"

4.  Model resolves "its" → "6205" from context
    └── Calls LookupAttribute("6205", "bore diameter")
        └── Returns: {"found": true, "name": "Bore diameter", "value": "25", "unit": "mm"}

5.  Returns: "The bore diameter of the 6205 bearing is 25 mm."
```

### Feedback turn (e.g. "That answer was wrong — please store my correction")

```
1.  ConversationOrchestrator
    ├── Fast-path: message contains "wrong" → intent = "feedback" immediately
    └── Routes to FeedbackAgent

2.  FeedbackAgent
    ├── Injects [Context]: last product = 6205, last attribute = width
    └── Model calls StoreFeedback("abc123", "6205", "width", "User says answer was wrong")
        └── FeedbackPlugin writes to Redis (or in-memory)

3.  Returns: "Thanks — your feedback for `6205 / width` has been saved."
```

### Cache hit (repeated question)

```
1.  QAAgent calls GetCachedAnswer("6205:width")
    └── Cache hit → returns "The width of the 6205 bearing is 15 mm."
        → Model returns the cached answer immediately
        → LookupAttribute is never called (saves one AI round-trip)
```

---

## Key Design Decisions

| Decision | Why |
|----------|-----|
| **AI-powered attribute extraction** | Instead of maintaining hardcoded keyword aliases and scoring logic in C#, the full product JSON is passed to Azure OpenAI for extraction. This handles natural language, abbreviations, and new datasheet formats without code changes. |
| **Single HTTP endpoint** | Keeps the API surface minimal. The orchestrator handles routing internally — clients don't need to know whether their message is a question or feedback. |
| **Function-scoped tool access** | `QAAgent` only sees datasheet + cache tools; `FeedbackAgent` only sees the feedback tool. This prevents the model from accidentally calling the wrong tool and reduces prompt confusion. |
| **Conversation state in memory** | Simple and zero-dependency for a single-instance deployment. The `IConversationStateService` interface means it can be swapped for a Redis-backed implementation without touching agents. |
| **Redis is optional** | `USE_REDIS=false` (default) runs everything in-process — useful for local dev and testing with no external dependencies. `USE_REDIS=true` switches both cache and feedback store to Redis. |
| **Transient Kernel per request** | A new `Kernel` instance is created per request so plugin registrations don't interfere across concurrent calls. The plugins themselves are singletons (stateless after construction). |
| **Fast-path intent classification** | Obvious feedback keywords skip the Azure OpenAI classification call entirely, cutting latency and token cost for common feedback patterns. |
| **No hallucination through tool gating** | The QA agent's system prompt mandates calling `LookupAttribute` before stating any value. The AI cannot answer from training data — it must go through the tool. |

---

## Assumptions

- Product designations in the filenames and JSON `"designation"` field are consistent (used as the lookup key).
- The Azure OpenAI deployment supports function calling (GPT-4 class or later).
- Conversation state is per-instance — not shared across multiple Function host instances. For multi-instance deployments, swap `ConversationStateService` for a Redis-backed equivalent.
- Feedback is write-only at this stage — there is no read/query endpoint for stored feedback.

---

## Setup and Configuration

### Prerequisites

| Tool | Version | Install |
|------|---------|---------|
| .NET SDK | 10.0+ | [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0) |
| Azure Functions Core Tools | v4 | `npm install -g azure-functions-core-tools@4` |
| Azure OpenAI resource | — | Deploy a chat model (GPT-4 class) |
| Redis (optional) | — | Azure Cache for Redis or local via Docker |

### Local development

```bash
# 1. Clone and enter the project
cd SKFProductAssistant

# 2. Verify local.settings.json has your credentials (see variables below)
#    Never commit this file — it contains secrets

# 3. Build
dotnet build

# 4. Start
func start
```

The endpoint is ready at `http://localhost:7071/api/chat`.

### Test requests (PowerShell)

```powershell
# First question — save the sessionId from the response
$r = Invoke-RestMethod -Method Post -Uri http://localhost:7071/api/chat `
       -ContentType "application/json" `
       -Body '{"message": "What is the width of 6205?"}'
$r  # prints answer, sessionId, intent

# Follow-up using the same session
Invoke-RestMethod -Method Post -Uri http://localhost:7071/api/chat `
  -ContentType "application/json" `
  -Body "{`"message`": `"And its bore diameter?`", `"sessionId`": `"$($r.sessionId)`"}"

# Leave feedback
Invoke-RestMethod -Method Post -Uri http://localhost:7071/api/chat `
  -ContentType "application/json" `
  -Body "{`"message`": `"That was wrong, please store my correction.`", `"sessionId`": `"$($r.sessionId)`"}"

# Not found
Invoke-RestMethod -Method Post -Uri http://localhost:7071/api/chat `
  -ContentType "application/json" `
  -Body '{"message": "What is the width of 9999?"}'
```

### Environment variables

Set in `local.settings.json` locally, or as Application Settings in Azure.

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `AZURE_OPENAI_ENDPOINT` | Yes | — | Azure OpenAI resource endpoint URL |
| `AZURE_OPENAI_API_KEY` | Yes | — | Azure OpenAI API key |
| `AZURE_OPENAI_DEPLOYMENT` | Yes | — | Chat deployment name (e.g. `Akash_Kumar`) |
| `REDIS_CONNECTION_STRING` | If `USE_REDIS=true` | — | StackExchange.Redis connection string |
| `USE_REDIS` | No | `false` | `true` to use Redis for cache and feedback |
| `CACHE_TTL_SECONDS` | No | `3600` | Cache entry lifetime in seconds |
| `MAX_HISTORY_TURNS` | No | `10` | Prior turns included in the QA agent's context |
| `MAX_MESSAGE_LENGTH` | No | `2000` | Max accepted message length (input guard) |

> **Security:** `local.settings.json` contains API keys. Add it to `.gitignore` — never commit it.

---

## Caching

The cache sits between the QA agent and the datasheet tool. The agent always checks it first:

```
GetCachedAnswer("6205:width") → hit → return immediately (no AI call)
                               → miss → call LookupAttribute → cache result
```

- **In-memory** (default): `ConcurrentDictionary` with TTL checked at read time. Fast, zero setup, lost on restart. Good for development.
- **Redis** (`USE_REDIS=true`): Survives restarts, shared across instances. Use in production.
- Cache keys are `{designation}:{attribute}` in lowercase, e.g. `6205:width`, `6205 n:limiting speed`.

---

## Hallucination Prevention

Five layers work together:

1. **Tool gating** — the QA agent is instructed to always call `LookupAttribute` or `GetRawProductData` before stating any value. It cannot answer from model training data.
2. **Extraction prompt** — the `LookupAttribute` extraction call explicitly says: *"Only use values that appear in the JSON — never guess or estimate."*
3. **`found: false` handling** — when the extraction returns `found: false`, the agent replies "I can't find that" instead of filling in a value.
4. **Function scoping** — `FunctionChoiceBehavior.Auto(functions: ...)` limits each agent to its own tools.
5. **Prompt injection guard** — system prompts include an explicit rule telling the model to ignore user instructions that attempt to override the rules.

---

## Project Structure

```
SKFProductAssistant/
├── Program.cs                      DI registration
├── HttpTrigger.cs                  POST /api/chat endpoint
├── host.json / local.settings.json Azure Functions config
│
├── Orchestrator/
│   └── ConversationOrchestrator.cs Intent classification + routing
│
├── Agents/
│   ├── IAgent.cs
│   ├── QAAgent.cs                  Answers product questions
│   └── FeedbackAgent.cs            Captures user feedback
│
├── Plugins/                        Semantic Kernel tool definitions
│   ├── DatasheetPlugin.cs          AI-powered attribute lookup
│   ├── CachePlugin.cs              Cache read/write tools
│   └── FeedbackPlugin.cs           Feedback persistence tool
│
├── Services/                       Interfaces + implementations
│   ├── ICacheService / InMemory / Redis
│   ├── IFeedbackStore / InMemory / Redis
│   └── IConversationStateService / ConversationStateService
│
├── Models/
│   ├── ChatRequest.cs / ChatResponse.cs
│   ├── ConversationState.cs
│   ├── FeedbackEntry.cs
│   └── AttributeLookupResult.cs   (documents the AI response contract)
│
└── Data/
    ├── 6205.json
    └── 6205 N.json
```
