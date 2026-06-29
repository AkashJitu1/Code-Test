using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;

namespace SKFProductAssistant.Plugins;

// Datasheets are read once at startup and kept as raw JSON strings in memory.
// Attribute extraction is AI-driven — the full product JSON is passed to the model
// so it can find values from natural language queries without any hardcoded aliases.
public sealed class DatasheetPlugin
{
    private readonly Dictionary<string, string> _productJsons;
    private readonly ILogger<DatasheetPlugin> _logger;

    // Instructs the extraction model to return a fixed JSON shape and never invent values.
    private const string ExtractionPrompt = """
        You are a precise data extractor for SKF product datasheets.

        Task: Find the value of "{attribute}" in the product datasheet JSON below.

        Rules:
        - Return ONLY a raw JSON object — no markdown, no explanation.
        - If found:   {"found": true,  "name": "<exact field name>", "value": "<value>", "unit": "<unit or null>"}
        - If not found: {"found": false, "message": "Attribute '{attribute}' not found in datasheet"}
        - Only use values that appear in the JSON — never guess or estimate.
        - If the query is ambiguous (e.g. "diameter" matches both "Outside diameter" and "Bore diameter"),
          return: {"found": true, "multiple": true, "matches": [{"name":"...","value":"...","unit":"..."},...]}

        Product datasheet:
        {json}
        """;

    public DatasheetPlugin(ILogger<DatasheetPlugin> logger)
    {
        _logger = logger;
        _productJsons = LoadAll();
    }

    // The Kernel parameter is automatically injected by Semantic Kernel when this function
    // is called as a tool — it gives us access to the chat service for the extraction call.
    [KernelFunction("LookupAttribute")]
    [Description("Look up a specific attribute of a product from its SKF datasheet. " +
                 "The AI reads the full datasheet and extracts the value dynamically. " +
                 "Use natural language — 'bore', 'width', 'limiting speed', 'dynamic load rating', etc.")]
    public async Task<string> LookupAttributeAsync(
        Kernel kernel,
        [Description("Product designation, e.g. '6205' or '6205 N'")]
        string designation,
        [Description("Attribute to look up in plain language, e.g. 'width', 'bore diameter', 'limiting speed'")]
        string attribute)
    {
        var key = Normalize(designation);
        if (!_productJsons.TryGetValue(key, out var productJson))
        {
            var available = string.Join(", ", _productJsons.Keys);
            return $"{{\"found\": false, \"message\": \"Product '{designation}' not found. Available: {available}\"}}";
        }

        var prompt = ExtractionPrompt
            .Replace("{attribute}", attribute)
            .Replace("{json}", productJson);

        try
        {
            var history = new ChatHistory();
            history.AddUserMessage(prompt);

            // No function calling here — we want a plain JSON string back, not another tool loop
            var settings = new AzureOpenAIPromptExecutionSettings
            {
                FunctionChoiceBehavior = FunctionChoiceBehavior.None()
            };

            var result = await kernel
                .GetRequiredService<IChatCompletionService>()
                .GetChatMessageContentAsync(history, settings, kernel);

            var response = result.Content?.Trim() ?? string.Empty;

            if (response.StartsWith("```"))
            {
                response = response.Trim('`');
                if (response.StartsWith("json"))
                    response = response[4..].TrimStart();
            }

            return string.IsNullOrWhiteSpace(response)
                ? $"{{\"found\": false, \"message\": \"No response from AI for '{attribute}'\"}}"
                : response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI extraction failed for {Designation}/{Attribute}", designation, attribute);
            return $"{{\"found\": false, \"message\": \"Could not extract '{attribute}' — please try again\"}}";
        }
    }

    [KernelFunction("GetRawProductData")]
    [Description("Returns the complete raw JSON datasheet for a product. " +
                 "Use this when you need multiple attributes at once or a full product overview.")]
    public string GetRawProductData(
        [Description("Product designation, e.g. '6205' or '6205 N'")]
        string designation)
    {
        var key = Normalize(designation);
        if (_productJsons.TryGetValue(key, out var json))
            return json;

        return $"Product '{designation}' not found. Available products: {string.Join(", ", _productJsons.Keys)}";
    }

    [KernelFunction("GetProductSummary")]
    [Description("Returns a brief summary of the product — category, description, and key benefits. " +
                 "Use this for general 'what is X?' questions.")]
    public string GetProductSummary(
        [Description("Product designation, e.g. '6205' or '6205 N'")]
        string designation)
    {
        var key = Normalize(designation);
        if (!_productJsons.TryGetValue(key, out var json))
            return $"Product '{designation}' not found in datasheets.";

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var des  = root.TryGetProperty("designation",       out var d) ? d.GetString() : designation;
            var cat  = root.TryGetProperty("category",          out var c) ? c.GetString() : "N/A";
            var desc = root.TryGetProperty("short_description", out var s) ? s.GetString() : "N/A";
            var ben  = root.TryGetProperty("benefits",          out var b) ? b.GetString() : "N/A";

            return $"Designation: {des}\nCategory: {cat}\nDescription: {desc}\nBenefits: {ben}";
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse summary for {Designation}", designation);
            return $"Product '{designation}' found but summary could not be read.";
        }
    }

    [KernelFunction("ListAvailableProducts")]
    [Description("Returns the list of product designations available in the local datasheets.")]
    public string ListAvailableProducts() =>
        "Available products: " + string.Join(", ", _productJsons.Keys);

    private static string Normalize(string designation) =>
        designation.Trim().ToUpperInvariant();

    private Dictionary<string, string> LoadAll()
    {
        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data");
        var result  = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(dataDir, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("designation", out var prop))
                {
                    var designation = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(designation))
                        result[Normalize(designation)] = json;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipped {File} — could not read or parse it", file);
            }
        }

        _logger.LogInformation("Loaded {Count} datasheets: {Designations}",
            result.Count, string.Join(", ", result.Keys));

        return result;
    }
}
