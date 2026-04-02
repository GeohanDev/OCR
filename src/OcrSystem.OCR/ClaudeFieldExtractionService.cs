using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OcrSystem.Application.DTOs;

namespace OcrSystem.OCR;

// ── Public contract ───────────────────────────────────────────────────────────

public interface IClaudeFieldExtractionService
{
    bool IsConfigured { get; }

    /// <summary>
    /// Step 5 — Claude full structured extraction.
    /// PaddleOCR produces raw text; Claude extracts ALL configured fields from it.
    /// Replaces the regex/keyword FieldExtractor when an Anthropic API key is present.
    /// AllowMultiple fields (table rows) are returned as one RawExtractedField per row.
    /// </summary>
    Task<IReadOnlyList<RawExtractedField>> ExtractFieldsAsync(
        string rawText,
        IReadOnlyList<FieldMappingConfigDto> fieldConfigs,
        CancellationToken ct = default);
}

// ── Implementation ────────────────────────────────────────────────────────────

public class ClaudeFieldExtractionService : IClaudeFieldExtractionService
{
    private readonly HttpClient _http;
    private readonly ILogger<ClaudeFieldExtractionService> _logger;
    private readonly string? _apiKey;
    private readonly string _primaryModel;
    private readonly string _fallbackModel;

    // Send up to 600 000 chars of OCR text (~150 000 tokens) — fits within Haiku's 200k context window
    // after accounting for the prompt template and field config overhead (~10k tokens).
    private const int MaxRawTextChars = 600_000;

    private const string AnthropicVersion = "2023-06-01";

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    public ClaudeFieldExtractionService(
        HttpClient http, IConfiguration config, ILogger<ClaudeFieldExtractionService> logger)
    {
        _http          = http;
        _logger        = logger;
        _apiKey        = config["Anthropic:ApiKey"];
        _primaryModel  = config["Anthropic:PrimaryModel"]  ?? "claude-haiku-4-5-20251001";
        _fallbackModel = config["Anthropic:FallbackModel"] ?? "claude-sonnet-4-6";
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

    public async Task<IReadOnlyList<RawExtractedField>> ExtractFieldsAsync(
        string rawText,
        IReadOnlyList<FieldMappingConfigDto> fieldConfigs,
        CancellationToken ct = default)
    {
        var activeConfigs = fieldConfigs.Where(c => c.IsActive).ToList();
        if (activeConfigs.Count == 0) return [];

        var truncated = rawText.Length > MaxRawTextChars
            ? rawText[..MaxRawTextChars] + "\n[text truncated]"
            : rawText;

        var prompt = BuildPrompt(truncated, activeConfigs);

        _logger.LogInformation(
            "Claude extraction (Step 5): extracting {Count} fields via {Model} ({Chars} chars of OCR text)",
            activeConfigs.Count, _primaryModel, truncated.Length);

        var responseText = await CallClaudeAsync(prompt, _primaryModel, ct);
        if (responseText is null)
        {
            _logger.LogWarning(
                "Claude extraction: primary model {Model} returned no result — waiting 3 s before fallback",
                _primaryModel);
            await Task.Delay(3_000, ct);
            responseText = await CallClaudeAsync(prompt, _fallbackModel, ct);
        }

        if (responseText is null)
        {
            _logger.LogWarning("Claude extraction returned no response from either model; returning empty fields");
            return BuildEmptyFields(activeConfigs);
        }

        _logger.LogDebug("Claude extraction response: {Text}", responseText);
        return ParseResponse(responseText, activeConfigs);
    }

    // ── Prompt construction ───────────────────────────────────────────────────

    private static string BuildPrompt(string rawText, IReadOnlyList<FieldMappingConfigDto> configs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a financial document field extraction specialist.");
        sb.AppendLine("Extract the requested fields from the OCR text of a vendor/supplier document.");
        sb.AppendLine();
        sb.AppendLine("FIELDS TO EXTRACT:");

        foreach (var c in configs)
        {
            var label = c.DisplayLabel ?? c.FieldName;
            var hints = BuildHints(c);

            if (c.AllowMultiple && c.IsCheckbox)
            {
                sb.AppendLine($"- {c.FieldName} | label: \"{label}\" | MULTIPLE ROWS, CHECKBOX: " +
                              $"return \"true\" if settled/paid/ticked, \"false\" if outstanding/unpaid/unticked{hints}");
            }
            else if (c.AllowMultiple)
            {
                sb.AppendLine($"- {c.FieldName} | label: \"{label}\" | MULTIPLE ROWS: extract ALL table rows as array{hints}");
            }
            else if (c.IsCheckbox)
            {
                sb.AppendLine($"- {c.FieldName} | label: \"{label}\" | CHECKBOX: \"true\"/\"false\"{hints}");
            }
            else
            {
                sb.AppendLine($"- {c.FieldName} | label: \"{label}\" | SINGLE VALUE{hints}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("RULES:");
        sb.AppendLine("- CRITICAL: ALL MULTIPLE ROWS arrays MUST be exactly the same length.");
        sb.AppendLine("- Align arrays by row index: index 0 = first data row, index 1 = second data row, etc.");
        sb.AppendLine("- If a row has no value for a column, use null — NEVER skip the row.");
        sb.AppendLine("  CORRECT: invoiceNumber has 5 rows, rows 2-4 are blank:");
        sb.AppendLine("    \"values\": [\"INV001\", null, null, null, \"INV005\"]");
        sb.AppendLine("  WRONG (breaks row alignment — do NOT do this):");
        sb.AppendLine("    \"values\": [\"INV001\", \"INV005\"]");
        sb.AppendLine("- For checkbox fields: use string \"true\" or \"false\".");
        sb.AppendLine("- For values not found in the document: use null.");
        sb.AppendLine("- Do NOT invent values absent from the document.");
        sb.AppendLine("- Ignore totals/summary rows — extract only individual transaction rows.");
        sb.AppendLine();
        sb.AppendLine("OCR TEXT (from PaddleOCR):");
        sb.AppendLine("---");
        sb.AppendLine(rawText);
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Return ONLY valid JSON, no markdown fences, no explanation:");
        sb.AppendLine("{");
        sb.AppendLine("  \"fields\": [");
        sb.AppendLine("    { \"fieldName\": \"singleFieldName\", \"value\": \"extracted value\", \"confidence\": 0.97 },");
        sb.AppendLine("    { \"fieldName\": \"multiFieldName\",  \"values\": [\"row1\", \"row2\"], \"confidence\": 0.95 }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <summary>
    /// Builds extraction hints from RegexPattern and KeywordAnchor config —
    /// the same signals the FieldExtractor used, now passed to Claude as guidance.
    /// </summary>
    private static string BuildHints(FieldMappingConfigDto c)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(c.KeywordAnchor))
            parts.Add($"near keyword \"{c.KeywordAnchor}\"");

        if (!string.IsNullOrWhiteSpace(c.RegexPattern))
            parts.Add($"matches pattern /{c.RegexPattern}/");

        return parts.Count > 0 ? " | hint: " + string.Join(", ", parts) : string.Empty;
    }

    // ── HTTP call to Anthropic ────────────────────────────────────────────────

    // Retry up to 3 times for transient Anthropic errors (429 rate-limit, 529 overloaded).
    // Delays: 2 s, 4 s, 8 s.
    private static readonly int[] RetryDelaysMs = [2_000, 4_000, 8_000];

    private async Task<string?> CallClaudeAsync(string prompt, string model, CancellationToken ct)
    {
        for (int attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
        {
            try
            {
                var requestBody = new JsonObject
                {
                    ["model"]      = model,
                    ["max_tokens"] = 64000,
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

                if (!response.IsSuccessStatusCode)
                {
                    var errBody = await response.Content.ReadAsStringAsync(ct);
                    var status  = (int)response.StatusCode;

                    // Transient: retry with backoff
                    if ((status == 429 || status == 529) && attempt < RetryDelaysMs.Length)
                    {
                        _logger.LogWarning(
                            "Claude API {Status} (transient) for model {Model} — retry {Attempt}/{Max} in {Delay} ms. Body: {Body}",
                            status, model, attempt + 1, RetryDelaysMs.Length,
                            RetryDelaysMs[attempt], errBody.Length > 300 ? errBody[..300] : errBody);
                        await Task.Delay(RetryDelaysMs[attempt], ct);
                        continue;
                    }

                    // Non-transient or retries exhausted
                    _logger.LogWarning(
                        "Claude API returned {Status} for model {Model} (attempt {Attempt}). Body: {Body}",
                        status, model, attempt + 1,
                        errBody.Length > 500 ? errBody[..500] : errBody);
                    return null;
                }

                var json        = await response.Content.ReadAsStringAsync(ct);
                var apiResponse = JsonSerializer.Deserialize<ClaudeApiResponse>(json, JsonOpts);
                return apiResponse?.Content?.FirstOrDefault(c => c.Type == "text")?.Text;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("Claude API call cancelled for model {Model}", model);
                return null;
            }
            catch (Exception ex)
            {
                if (attempt < RetryDelaysMs.Length)
                {
                    _logger.LogWarning(ex,
                        "Claude API call threw for model {Model} — retry {Attempt}/{Max} in {Delay} ms",
                        model, attempt + 1, RetryDelaysMs.Length, RetryDelaysMs[attempt]);
                    await Task.Delay(RetryDelaysMs[attempt], ct);
                    continue;
                }
                _logger.LogWarning(ex, "Claude API call failed for model {Model} after {Attempts} attempts",
                    model, attempt + 1);
                return null;
            }
        }
        return null;
    }

    // ── Parse Claude JSON response → RawExtractedField list ──────────────────

    private IReadOnlyList<RawExtractedField> ParseResponse(
        string responseText, IReadOnlyList<FieldMappingConfigDto> configs)
    {
        // Strip markdown fences if present
        var json = responseText.Trim();
        if (json.StartsWith("```"))
        {
            var start = json.IndexOf('{');
            var end   = json.LastIndexOf('}');
            if (start >= 0 && end > start) json = json[start..(end + 1)];
        }

        // Build a lookup: fieldName → ClaudeField
        var lookup = new Dictionary<string, ClaudeField>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("fields", out var fields))
            {
                foreach (var f in fields.EnumerateArray())
                {
                    var name  = f.TryGetProperty("fieldName", out var fn) ? fn.GetString() : null;
                    var conf  = f.TryGetProperty("confidence", out var cf) ? (float)cf.GetDouble() : 0.85f;
                    if (name is null) continue;

                    // Multi-value field
                    if (f.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
                    {
                        var values = vals.EnumerateArray()
                            .Select(v => v.ValueKind == JsonValueKind.Null ? null : v.GetString())
                            .ToList();
                        lookup[name] = new ClaudeField(name, null, values, conf);
                    }
                    // Single-value field
                    else if (f.TryGetProperty("value", out var val))
                    {
                        var value = val.ValueKind == JsonValueKind.Null ? null : val.GetString();
                        lookup[name] = new ClaudeField(name, value, null, conf);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Claude extraction response; returning empty fields");
            return BuildEmptyFields(configs);
        }

        // ── Pass 1: collect AllowMultiple arrays and find max row count ──────────
        // Claude may return shorter arrays for sparse columns (e.g. invoiceNumber only
        // present in 2 out of 5 rows). Pad all arrays to the same length so row indices
        // align correctly in the UI table.
        var multiArrays = new Dictionary<string, (FieldMappingConfigDto Config, List<string?> Values, float Confidence)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var config in configs.Where(c => c.AllowMultiple))
        {
            if (!lookup.TryGetValue(config.FieldName, out var field)) continue;
            var values = field.Values ?? (field.Value is not null ? new List<string?> { field.Value } : new List<string?>());
            multiArrays[config.FieldName] = (config, values, field.Confidence);
        }

        // Pad all AllowMultiple arrays to the same max length
        var maxRows = multiArrays.Values.Select(x => x.Values.Count).DefaultIfEmpty(0).Max();
        foreach (var key in multiArrays.Keys.ToList())
        {
            var (cfg, values, conf) = multiArrays[key];
            while (values.Count < maxRows) values.Add(null);
            multiArrays[key] = (cfg, values, conf);
        }

        // ── Pass 2: emit results ──────────────────────────────────────────────
        var results = new List<RawExtractedField>();

        foreach (var config in configs)
        {
            if (!lookup.TryGetValue(config.FieldName, out var field))
            {
                // Field not in Claude response — add null placeholder(s)
                if (config.AllowMultiple && maxRows > 0)
                {
                    for (var i = 0; i < maxRows; i++)
                        results.Add(new RawExtractedField(
                            config.Id, config.FieldName, null, "ClaudeExtraction", 0.0f, null, null));
                }
                else
                {
                    results.Add(new RawExtractedField(
                        config.Id, config.FieldName, null, "ClaudeExtraction", 0.0f, null, null));
                }
                continue;
            }

            if (config.AllowMultiple)
            {
                // Use the padded array from Pass 1 (fallback to empty if somehow missing)
                if (!multiArrays.TryGetValue(config.FieldName, out var multi))
                {
                    results.Add(new RawExtractedField(
                        config.Id, config.FieldName, null, "ClaudeExtraction", 0.0f, null, null));
                    continue;
                }
                var (_, values, confidence) = multi;
                if (values.Count == 0)
                {
                    results.Add(new RawExtractedField(
                        config.Id, config.FieldName, null, "ClaudeExtraction", 0.0f, null, null));
                }
                else
                {
                    foreach (var v in values)
                        results.Add(new RawExtractedField(
                            config.Id, config.FieldName, v?.Trim(), "ClaudeExtraction", confidence, null, null));
                }
            }
            else
            {
                // Single-value field
                var value = field.Value?.Trim();
                results.Add(new RawExtractedField(
                    config.Id, config.FieldName, value, "ClaudeExtraction",
                    string.IsNullOrEmpty(value) ? 0.0f : field.Confidence, null, null));
            }
        }

        var totalFields = results.Count;
        var foundFields = results.Count(r => !string.IsNullOrEmpty(r.RawValue));
        _logger.LogInformation(
            "Claude extraction complete: {Found}/{Total} field values extracted",
            foundFields, totalFields);

        return results;
    }

    private static IReadOnlyList<RawExtractedField> BuildEmptyFields(
        IReadOnlyList<FieldMappingConfigDto> configs) =>
        configs.Select(c => new RawExtractedField(
            c.Id, c.FieldName, null, "ClaudeExtraction", 0.0f, null, null)).ToList();

    // ── Internal models ───────────────────────────────────────────────────────

    private record ClaudeField(string FieldName, string? Value, List<string?>? Values, float Confidence);

    private record ClaudeApiResponse(
        [property: JsonPropertyName("content")] List<ClaudeContent>? Content);

    private record ClaudeContent(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);
}
