using System.Text;
using System.Text.RegularExpressions;
using OcrSystem.Application.DTOs;

namespace OcrSystem.OCR;

public interface IFieldExtractor
{
    IReadOnlyList<RawExtractedField> ExtractFields(TesseractOutput ocr, IReadOnlyList<FieldMappingConfigDto> configs);
}

public record RawExtractedField(
    Guid FieldMappingConfigId,
    string FieldName,
    string? RawValue,
    string ExtractionStrategy,
    float StrategyConfidence,
    OcrBoundingBox? BoundingBox,
    int? Page);

public class FieldExtractor : IFieldExtractor
{
    // Lines containing these keywords signal the end of the data table.
    private static readonly string[] TableEndMarkers =
    [
        "total", "grand total", "sub-total", "subtotal", "net total",
        "balance due", "amount due", "outstanding balance", "closing balance",
        "brought forward", "carry forward", "page total", "payment", "remittance"
    ];

    public IReadOnlyList<RawExtractedField> ExtractFields(TesseractOutput ocr, IReadOnlyList<FieldMappingConfigDto> configs)
    {
        var results = new List<RawExtractedField>();

        foreach (var config in configs.Where(c => c.IsActive))
        {
            // ── AllowMultiple + Regex: collect all regex matches ─────────────
            if (config.AllowMultiple && !string.IsNullOrWhiteSpace(config.RegexPattern))
            {
                try
                {
                    var matches = Regex.Matches(ocr.FullText, config.RegexPattern,
                        RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromSeconds(2));
                    foreach (Match m in matches)
                    {
                        var value = m.Groups.Count > 1 ? m.Groups[1].Value : m.Value;
                        results.Add(new RawExtractedField(
                            config.Id, config.FieldName, value.Trim(), "Regex", 1.0f, null, null));
                    }
                }
                catch (RegexMatchTimeoutException) { }

                if (!results.Any(r => r.FieldMappingConfigId == config.Id))
                    results.Add(new RawExtractedField(config.Id, config.FieldName, null, "None", 0.0f, null, null));

                continue;
            }

            // ── AllowMultiple + KeywordAnchor (no regex): column extraction ──
            // Finds the column header, notes its horizontal character position,
            // then extracts the token nearest that position from every data row.
            // Stops at table-end keywords or 2+ consecutive blank lines.
            // Finally filters the raw candidates to keep only values whose
            // structural shape matches the dominant format in the column.
            if (config.AllowMultiple && !string.IsNullOrWhiteSpace(config.KeywordAnchor)
                && string.IsNullOrWhiteSpace(config.RegexPattern))
            {
                var anchors = config.KeywordAnchor
                    .Split(new[] { ',', '|' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                var lines = ocr.FullText.Split('\n', StringSplitOptions.None);
                const int Tolerance = 15;

                foreach (var anchor in anchors)
                {
                    int headerLineIdx = -1;
                    int colStart = -1;

                    for (int i = 0; i < lines.Length; i++)
                    {
                        var idx = lines[i].IndexOf(anchor, StringComparison.OrdinalIgnoreCase);
                        if (idx < 0) continue;
                        headerLineIdx = i;
                        colStart = idx;
                        break;
                    }

                    if (headerLineIdx < 0) continue;

                    var candidates = new List<string>();
                    int consecutiveBlank = 0;

                    for (int i = headerLineIdx + 1; i < lines.Length; i++)
                    {
                        var line = lines[i];

                        // Track blank lines — 2+ in a row means the table has ended
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            if (++consecutiveBlank >= 2) break;
                            continue;
                        }
                        consecutiveBlank = 0;

                        // Stop at totals / summary / footer lines
                        if (IsTableEndLine(line)) break;

                        // Pick the token whose start position is closest to colStart
                        var bestToken = Regex.Matches(line, @"\S+")
                            .Cast<Match>()
                            .Where(m => m.Index >= colStart - Tolerance && m.Index <= colStart + Tolerance)
                            .OrderBy(m => Math.Abs(m.Index - colStart))
                            .FirstOrDefault();

                        if (bestToken is null) continue;

                        var value = bestToken.Value.Trim();
                        if (!string.IsNullOrEmpty(value))
                            candidates.Add(value);
                    }

                    // Filter to values that share the dominant structural shape
                    foreach (var v in FilterByShape(candidates))
                        results.Add(new RawExtractedField(
                            config.Id, config.FieldName, v, "Column", 0.85f, null, null));

                    if (results.Any(r => r.FieldMappingConfigId == config.Id)) break;
                }

                if (!results.Any(r => r.FieldMappingConfigId == config.Id))
                    results.Add(new RawExtractedField(config.Id, config.FieldName, null, "None", 0.0f, null, null));

                continue;
            }

            // ── Single-value extraction ──────────────────────────────────────
            RawExtractedField? extracted = null;

            // Strategy 1: Regex
            if (!string.IsNullOrWhiteSpace(config.RegexPattern))
            {
                try
                {
                    var match = Regex.Match(ocr.FullText, config.RegexPattern,
                        RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeSpan.FromSeconds(1));
                    if (match.Success)
                    {
                        var value = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
                        extracted = new RawExtractedField(
                            config.Id, config.FieldName, value.Trim(), "Regex", 1.0f, null, null);
                    }
                }
                catch (RegexMatchTimeoutException) { }
            }

            // Strategy 2: Keyword anchor
            if (extracted is null && !string.IsNullOrWhiteSpace(config.KeywordAnchor))
            {
                var anchor = ocr.Blocks.FirstOrDefault(b =>
                    b.Text.Contains(config.KeywordAnchor, StringComparison.OrdinalIgnoreCase));
                if (anchor is not null)
                {
                    var nearby = ocr.Blocks
                        .Where(b => b != anchor && b.Page == anchor.Page &&
                                    b.BoundingBox.Y >= anchor.BoundingBox.Y - 10 &&
                                    b.BoundingBox.Y <= anchor.BoundingBox.Y + anchor.BoundingBox.Height + 30 &&
                                    b.BoundingBox.X > anchor.BoundingBox.X)
                        .OrderBy(b => b.BoundingBox.X)
                        .FirstOrDefault();

                    if (nearby is not null)
                    {
                        var distance = Math.Abs(nearby.BoundingBox.X - anchor.BoundingBox.X)
                                     + Math.Abs(nearby.BoundingBox.Y - anchor.BoundingBox.Y);
                        var conf = distance < 50 ? 1.0f : distance < 150 ? 0.7f : 0.3f;
                        extracted = new RawExtractedField(
                            config.Id, config.FieldName, nearby.Text,
                            "Keyword", conf, nearby.BoundingBox, nearby.Page);
                    }
                }
            }

            // Strategy 3: Position rule (placeholder)
            if (extracted is null && config.PositionRule is not null)
            {
                extracted = new RawExtractedField(
                    config.Id, config.FieldName, null, "Position", 0.0f, null, null);
            }

            results.Add(extracted ?? new RawExtractedField(
                config.Id, config.FieldName, null, "None", 0.0f, null, null));
        }

        return results;
    }

    // Returns true when the line signals the end of the data table.
    private static bool IsTableEndLine(string line) =>
        TableEndMarkers.Any(m => line.Contains(m, StringComparison.OrdinalIgnoreCase));

    // Keeps only values whose structural shape matches the dominant format.
    //
    // Each value is reduced to a shape string where consecutive characters of
    // the same class are collapsed to one letter:
    //   L = letter, D = digit, S = separator (dash / slash / dot / etc.)
    //
    // Example shapes:
    //   "INV-001"      → "LSD"   (Letters · Separator · Digits)
    //   "2024-INV-001" → "DSLSD"
    //   "01/01/2024"   → "DSDSD"  ← date — filtered out when "LSD" dominates
    //   "1,250.00"     → "DSDS"   ← amount — filtered out
    //
    // Pre-filters remove obvious non-document-number tokens (dates, decimal
    // amounts, very short tokens) before shape grouping so they cannot skew
    // the dominant shape calculation.
    private static IReadOnlyList<string> FilterByShape(List<string> rawValues)
    {
        if (rawValues.Count == 0) return rawValues;

        // Pre-filter: discard tokens that are clearly not document numbers
        var candidates = rawValues
            .Where(v => v.Length >= 2 && v.Length <= 30)
            .Where(v => char.IsLetterOrDigit(v[0]))
            .Where(v => !Regex.IsMatch(v, @"^\d{1,2}[\/\-]\d{1,2}"))   // date dd/mm
            .Where(v => !Regex.IsMatch(v, @"^\d[\d,]*\.\d{2}$"))        // decimal amount
            .ToList();

        if (candidates.Count == 0) return rawValues; // fallback: nothing passed filter

        // Find the most common structural shape
        var shaped = candidates
            .Select(v => (Value: v, Shape: TokenShape(v)))
            .ToList();

        var dominantShape = shaped
            .GroupBy(x => x.Shape)
            .OrderByDescending(g => g.Count())
            .First().Key;

        var result = shaped
            .Where(x => x.Shape == dominantShape)
            .Select(x => x.Value)
            .ToList();

        return result.Count > 0 ? result : candidates;
    }

    // Collapses a token into a shape string: consecutive same-class chars → one letter.
    private static string TokenShape(string value)
    {
        var sb = new StringBuilder();
        char prev = '\0';
        foreach (char c in value)
        {
            char cat = char.IsLetter(c) ? 'L' : char.IsDigit(c) ? 'D' : 'S';
            if (cat != prev) { sb.Append(cat); prev = cat; }
        }
        return sb.ToString();
    }
}
