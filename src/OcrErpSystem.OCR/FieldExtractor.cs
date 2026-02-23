using System.Text.RegularExpressions;
using OcrErpSystem.Application.DTOs;

namespace OcrErpSystem.OCR;

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
    public IReadOnlyList<RawExtractedField> ExtractFields(TesseractOutput ocr, IReadOnlyList<FieldMappingConfigDto> configs)
    {
        var results = new List<RawExtractedField>();

        foreach (var config in configs.Where(c => c.IsActive))
        {
            RawExtractedField? extracted = null;

            // Strategy 1: Regex (highest priority)
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
                catch (RegexMatchTimeoutException) { /* fall through */ }
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

            // Strategy 3: Position rule (placeholder — would crop & re-OCR in production)
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
}
