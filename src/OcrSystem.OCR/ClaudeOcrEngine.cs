using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OcrSystem.Application.DTOs;

namespace OcrSystem.OCR;

public interface IClaudeOcrEngine
{
    bool IsConfigured { get; }
    Task<ClaudeExtractionResult> ExtractAsync(
        IReadOnlyList<ProcessedPageImage> pages,
        IReadOnlyList<FieldMappingConfigDto> fieldConfigs,
        CancellationToken ct = default);
}

public record ClaudeExtractionResult(
    string FullText,
    IReadOnlyList<RawExtractedField> Fields,
    double OverallConfidence,
    string ModelUsed);

public class ClaudeOcrEngine : IClaudeOcrEngine
{
    private readonly HttpClient _http;
    private readonly ILogger<ClaudeOcrEngine> _logger;
    private readonly string? _apiKey;
    private readonly string _primaryModel;
    private readonly string _fallbackModel;

    private const string AnthropicVersion = "2023-06-01";
    private const double EscalationThreshold = 0.7;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    public ClaudeOcrEngine(HttpClient http, IConfiguration config, ILogger<ClaudeOcrEngine> logger)
    {
        _http = http;
        _logger = logger;
        _apiKey = config["Anthropic:ApiKey"];
        _primaryModel = config["Anthropic:PrimaryModel"] ?? "claude-haiku-4-5-20251001";
        _fallbackModel = config["Anthropic:FallbackModel"] ?? "claude-sonnet-4-6";
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<ClaudeExtractionResult> ExtractAsync(
        IReadOnlyList<ProcessedPageImage> pages,
        IReadOnlyList<FieldMappingConfigDto> fieldConfigs,
        CancellationToken ct = default)
    {
        var prompt = BuildPrompt(fieldConfigs);

        // Digital PDF: all pages have PdfPig-extracted text — use text API (faster, cheaper).
        // Scanned: pages contain image bytes — use Vision API.
        bool isDigital = pages.All(p => p.PreExtractedBlocks is { Count: > 0 });

        var result = await CallClaudeAsync(pages, prompt, fieldConfigs, _primaryModel, isDigital, ct);

        // Escalate to the more capable model when confidence is too low
        if (result.OverallConfidence < EscalationThreshold)
        {
            _logger.LogInformation(
                "Claude confidence {C:F2} below threshold; escalating from {P} to {F}",
                result.OverallConfidence, _primaryModel, _fallbackModel);
            result = await CallClaudeAsync(pages, prompt, fieldConfigs, _fallbackModel, isDigital, ct);
        }

        return result;
    }

    private async Task<ClaudeExtractionResult> CallClaudeAsync(
        IReadOnlyList<ProcessedPageImage> pages,
        string prompt,
        IReadOnlyList<FieldMappingConfigDto> fieldConfigs,
        string model,
        bool isDigital,
        CancellationToken ct)
    {
        var contentArray = BuildContentArray(isDigital, prompt, pages);

        var requestBody = new JsonObject
        {
            ["model"]      = model,
            ["max_tokens"] = 8192,
            ["messages"]   = new JsonArray
            {
                new JsonObject
                {
                    ["role"]    = "user",
                    ["content"] = contentArray
                }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/messages")
        {
            Content = new StringContent(requestBody.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-api-key", _apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        var apiResponse = JsonSerializer.Deserialize<ClaudeApiResponse>(json, JsonOpts);
        var responseText = apiResponse?.Content?.FirstOrDefault(c => c.Type == "text")?.Text ?? "{}";

        _logger.LogDebug("Claude ({M}) response length: {L} chars", model, responseText.Length);
        return ParseResponse(responseText, fieldConfigs, model);
    }

    private static JsonArray BuildContentArray(
        bool isDigital, string prompt, IReadOnlyList<ProcessedPageImage> pages)
    {
        var array = new JsonArray();

        if (isDigital)
        {
            var docText = string.Join("\n\n--- Page Break ---\n\n",
                pages.Select(p => string.Join(" ", p.PreExtractedBlocks!.Select(b => b.Text))));

            array.Add(JsonSerializer.SerializeToNode(
                new { type = "text", text = $"{prompt}\n\nDocument text:\n{docText}" }));
        }
        else
        {
            array.Add(JsonSerializer.SerializeToNode(new { type = "text", text = prompt }));

            foreach (var page in pages)
            {
                array.Add(JsonSerializer.SerializeToNode(new
                {
                    type   = "image",
                    source = new
                    {
                        type       = "base64",
                        media_type = "image/png",
                        data       = Convert.ToBase64String(page.ImageData)
                    }
                }));
            }
        }

        return array;
    }

    private static string BuildPrompt(IReadOnlyList<FieldMappingConfigDto> configs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a document data extraction system. Extract ALL data visible in the document.");
        sb.AppendLine();

        var activeConfigs = configs.Where(c => c.IsActive).ToList();
        var checkboxConfigs = activeConfigs.Where(c => c.IsCheckbox).ToList();

        if (activeConfigs.Count > 0)
        {
            sb.AppendLine("Use these EXACT fieldNames for the following fields:");
            foreach (var c in activeConfigs)
            {
                sb.Append($"- {c.DisplayLabel ?? c.FieldName} (fieldName: \"{c.FieldName}\"");
                if (c.AllowMultiple)
                    sb.Append(", extract ALL occurrences as a separate array entry per row");
                if (c.IsCheckbox)
                    sb.Append(", BOOLEAN FIELD: output \"true\" or \"false\" only");
                if (!string.IsNullOrWhiteSpace(c.KeywordAnchor))
                    sb.Append($", look near: \"{c.KeywordAnchor}\"");
                if (!string.IsNullOrWhiteSpace(c.RegexPattern))
                    sb.Append($", pattern hint: {c.RegexPattern}");
                sb.AppendLine(")");
            }
            sb.AppendLine();
        }

        if (checkboxConfigs.Count > 0)
        {
            sb.AppendLine("BOOLEAN FIELD RULES — for each field marked as BOOLEAN:");
            sb.AppendLine("Output \"true\" if the row shows any of these settlement/payment indicators:");
            sb.AppendLine("  - A \"PAID\", \"P\", \"C\", \"CR\", \"CLR\", \"Cleared\" or \"Settled\" marker in any column");
            sb.AppendLine("  - Invoice reference prefixed with CR, CN, or RCN (credit note)");
            sb.AppendLine("  - Outstanding balance of 0.00 or 0 while an invoice amount exists");
            sb.AppendLine("  - A payment or credit note reference number in a remittance column");
            sb.AppendLine("Output \"false\" for all other rows (outstanding / unpaid invoices).");
            sb.AppendLine("One \"true\" or \"false\" value per table row — same row count as other table fields.");
            sb.AppendLine();
        }

        sb.AppendLine("For table rows, extract each row as a separate entry with the same fieldName.");
        sb.AppendLine("Only extract the fields listed above. Do not invent or add extra fields.");
        sb.AppendLine();
        sb.AppendLine("Return ONLY valid JSON (no markdown fences, no explanation) in exactly this schema:");
        sb.AppendLine("{");
        sb.AppendLine("  \"rawText\": \"full visible text of the document\",");
        sb.AppendLine("  \"overallConfidence\": 0.95,");
        sb.AppendLine("  \"fields\": [");
        sb.AppendLine("    { \"fieldName\": \"ExampleField\", \"values\": [\"value1\"], \"confidence\": 0.9 }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("Use an empty array [] when a field is not found. Confidence is 0.0–1.0.");
        return sb.ToString();
    }

    private static ClaudeExtractionResult ParseResponse(
        string text, IReadOnlyList<FieldMappingConfigDto> fieldConfigs, string model)
    {
        // Strip markdown fences in case Claude wrapped the JSON
        var json = text.Trim();
        if (json.StartsWith("```"))
        {
            var start = json.IndexOf('{');
            var end   = json.LastIndexOf('}');
            if (start >= 0 && end > start) json = json[start..(end + 1)];
        }

        ClaudeJsonResponse? parsed = null;
        try { parsed = JsonSerializer.Deserialize<ClaudeJsonResponse>(json, JsonOpts); }
        catch { /* fall through with null */ }

        var fullText    = parsed?.RawText ?? text;
        var overallConf = parsed?.OverallConfidence ?? 0.5;
        var rawFields   = new List<RawExtractedField>();

        if (parsed?.Fields is not null)
        {
            foreach (var field in parsed.Fields)
            {
                var config = fieldConfigs.FirstOrDefault(c =>
                    string.Equals(c.FieldName, field.FieldName, StringComparison.OrdinalIgnoreCase));

                if (config is null) continue; // skip anything not in the field mapping config

                var values = field.Values ?? [];

                if (values.Count == 0)
                {
                    rawFields.Add(new RawExtractedField(
                        config.Id, field.FieldName, null, "Claude", 0.0f, null, null));
                }
                else if (config.AllowMultiple)
                {
                    foreach (var v in values)
                        rawFields.Add(new RawExtractedField(
                            config.Id, field.FieldName, v, "Claude", (float)field.Confidence, null, null));
                }
                else
                {
                    rawFields.Add(new RawExtractedField(
                        config.Id, field.FieldName, values[0], "Claude", (float)field.Confidence, null, null));
                }
            }
        }

        // Ensure every active config has at least a "None" placeholder
        foreach (var config in fieldConfigs.Where(c => c.IsActive))
        {
            if (!rawFields.Any(f => f.FieldMappingConfigId == config.Id))
                rawFields.Add(new RawExtractedField(
                    config.Id, config.FieldName, null, "None", 0.0f, null, null));
        }

        return new ClaudeExtractionResult(fullText, rawFields, overallConf, model);
    }

    // ── Anthropic API response models ─────────────────────────────────────────

    private record ClaudeApiResponse(
        [property: JsonPropertyName("content")] List<ClaudeContent>? Content);

    private record ClaudeContent(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);

    // ── Structured JSON returned by Claude ────────────────────────────────────

    private record ClaudeJsonResponse(
        [property: JsonPropertyName("rawText")]           string? RawText,
        [property: JsonPropertyName("overallConfidence")] double OverallConfidence,
        [property: JsonPropertyName("fields")]            List<ClaudeField>? Fields);

    private record ClaudeField(
        [property: JsonPropertyName("fieldName")]  string FieldName,
        [property: JsonPropertyName("values")]     List<string>? Values,
        [property: JsonPropertyName("confidence")] double Confidence);
}
