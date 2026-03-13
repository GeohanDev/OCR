using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OcrSystem.Application.DTOs;

namespace OcrSystem.OCR;

// ── Public contract ───────────────────────────────────────────────────────────

public interface IClaudeFieldCorrectionService
{
    bool IsConfigured { get; }

    /// <summary>
    /// Step 5 — AI Validation Layer.
    /// Uses the full OCR rawText as context to correct low-confidence extracted fields.
    /// Only non-AllowMultiple (header) fields are corrected; table rows are left as-is.
    /// Returns the same list with corrected values and updated confidence scores.
    /// </summary>
    Task<IReadOnlyList<RawExtractedField>> CorrectFieldsAsync(
        string rawText,
        IReadOnlyList<RawExtractedField> fields,
        IReadOnlyList<FieldMappingConfigDto> fieldConfigs,
        CancellationToken ct = default);
}

// ── Implementation ────────────────────────────────────────────────────────────

public class ClaudeFieldCorrectionService : IClaudeFieldCorrectionService
{
    private readonly HttpClient _http;
    private readonly ILogger<ClaudeFieldCorrectionService> _logger;
    private readonly string? _apiKey;
    private readonly string _model;

    // Only correct fields whose OCR confidence is below this threshold.
    private const double CorrectionThreshold = 0.75;
    // Cap rawText at ~3 000 chars to stay within cheap token budget.
    private const int MaxRawTextChars = 3000;

    private const string AnthropicVersion = "2023-06-01";

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    public ClaudeFieldCorrectionService(
        HttpClient http, IConfiguration config, ILogger<ClaudeFieldCorrectionService> logger)
    {
        _http   = http;
        _logger = logger;
        _apiKey = config["Anthropic:ApiKey"];
        // Reuse the primary model (Haiku) — text-only correction is fast and cheap.
        _model  = config["Anthropic:PrimaryModel"] ?? "claude-haiku-4-5-20251001";
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<IReadOnlyList<RawExtractedField>> CorrectFieldsAsync(
        string rawText,
        IReadOnlyList<RawExtractedField> fields,
        IReadOnlyList<FieldMappingConfigDto> fieldConfigs,
        CancellationToken ct = default)
    {
        // Only process single-value (non-AllowMultiple) fields with low confidence.
        // AllowMultiple table rows have variable counts and are better left to the
        // field extractor's position/regex logic.
        var lowConfSingle = fields
            .Where(f =>
            {
                var cfg = fieldConfigs.FirstOrDefault(c => c.Id == f.FieldMappingConfigId);
                return cfg is { AllowMultiple: false, IsCheckbox: false }
                    && (double)f.StrategyConfidence < CorrectionThreshold;
            })
            .ToList();

        if (lowConfSingle.Count == 0)
            return fields;

        _logger.LogInformation(
            "Claude correction (Step 5): correcting {Count} low-confidence fields via {Model}",
            lowConfSingle.Count, _model);

        var prompt = BuildPrompt(rawText, lowConfSingle, fieldConfigs);

        var requestBody = new JsonObject
        {
            ["model"]      = _model,
            ["max_tokens"] = 1024,
            ["messages"]   = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = prompt }
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

        var json         = await response.Content.ReadAsStringAsync(ct);
        var apiResponse  = JsonSerializer.Deserialize<ClaudeApiResponse>(json, JsonOpts);
        var responseText = apiResponse?.Content?.FirstOrDefault(c => c.Type == "text")?.Text ?? "{}";

        _logger.LogDebug("Claude correction response: {Text}", responseText);
        return ApplyCorrections(responseText, fields, lowConfSingle);
    }

    // ── Prompt construction ───────────────────────────────────────────────────

    private static string BuildPrompt(
        string rawText,
        IReadOnlyList<RawExtractedField> lowConfFields,
        IReadOnlyList<FieldMappingConfigDto> fieldConfigs)
    {
        var truncated = rawText.Length > MaxRawTextChars
            ? rawText[..MaxRawTextChars] + "\n[text truncated]"
            : rawText;

        var sb = new StringBuilder();
        sb.AppendLine("You are a document field correction assistant.");
        sb.AppendLine("The OCR engine extracted the fields below with low confidence.");
        sb.AppendLine("Using the full document text as context, correct any OCR mis-reads.");
        sb.AppendLine();
        sb.AppendLine("Full document text (from OCR):");
        sb.AppendLine("---");
        sb.AppendLine(truncated);
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Fields to verify/correct:");
        foreach (var f in lowConfFields)
        {
            var cfg   = fieldConfigs.FirstOrDefault(c => c.Id == f.FieldMappingConfigId);
            var label = cfg?.DisplayLabel ?? f.FieldName;
            sb.AppendLine($"- {label} (fieldName: \"{f.FieldName}\", " +
                          $"extracted: \"{f.RawValue ?? "not found"}\", " +
                          $"confidence: {f.StrategyConfidence:F2})");
        }
        sb.AppendLine();
        sb.AppendLine("Return ONLY valid JSON (no markdown, no explanation):");
        sb.AppendLine("{");
        sb.AppendLine("  \"corrections\": [");
        sb.AppendLine("    { \"fieldName\": \"example\", \"correctedValue\": \"corrected\", \"confidence\": 0.95 }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("Rules:");
        sb.AppendLine("- If the extracted value is correct as-is, return it with higher confidence.");
        sb.AppendLine("- If the field is genuinely absent from the document, return null for correctedValue.");
        sb.AppendLine("- Do not invent values not present in the document text.");
        return sb.ToString();
    }

    // ── Apply corrections to the original field list ──────────────────────────

    private IReadOnlyList<RawExtractedField> ApplyCorrections(
        string responseText,
        IReadOnlyList<RawExtractedField> originalFields,
        IReadOnlyList<RawExtractedField> correctedSubset)
    {
        var json = responseText.Trim();
        // Strip markdown fences if Claude wrapped the JSON
        if (json.StartsWith("```"))
        {
            var start = json.IndexOf('{');
            var end   = json.LastIndexOf('}');
            if (start >= 0 && end > start) json = json[start..(end + 1)];
        }

        // Build a lookup: fieldName → (correctedValue, confidence)
        var lookup = new Dictionary<string, (string? Value, float Conf)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("corrections", out var corrections))
            {
                foreach (var c in corrections.EnumerateArray())
                {
                    var fieldName = c.TryGetProperty("fieldName",     out var fn) ? fn.GetString()    : null;
                    var corrected = c.TryGetProperty("correctedValue", out var cv)
                        && cv.ValueKind != JsonValueKind.Null ? cv.GetString() : null;
                    var conf      = c.TryGetProperty("confidence",    out var cf) ? (float)cf.GetDouble() : 0.8f;
                    if (fieldName is not null)
                        lookup[fieldName] = (corrected, conf);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Claude correction response; returning originals");
            return originalFields;
        }

        if (lookup.Count == 0) return originalFields;

        // Build a set of field IDs that are in the low-conf corrected subset
        var correctedIds = correctedSubset
            .Select(f => f.FieldMappingConfigId)
            .ToHashSet();

        return originalFields.Select(f =>
        {
            if (!correctedIds.Contains(f.FieldMappingConfigId)) return f;
            if (!lookup.TryGetValue(f.FieldName, out var correction)) return f;

            _logger.LogDebug(
                "Corrected {Field}: '{Old}' → '{New}' (conf {Old:F2} → {New:F2})",
                f.FieldName, f.RawValue, correction.Value,
                f.StrategyConfidence, correction.Conf);

            return f with
            {
                RawValue           = correction.Value,
                StrategyConfidence = correction.Conf,
                ExtractionStrategy = "ClaudeCorrection"
            };
        }).ToList();
    }

    // ── Anthropic response models ──────────────────────────────────────────────

    private record ClaudeApiResponse(
        [property: JsonPropertyName("content")] List<ClaudeContent>? Content);

    private record ClaudeContent(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);
}
