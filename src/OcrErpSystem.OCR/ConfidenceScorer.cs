namespace OcrErpSystem.OCR;

public interface IConfidenceScorer
{
    double Score(RawExtractedField field, IReadOnlyList<OcrBlock> blocks);
}

public class ConfidenceScorer : IConfidenceScorer
{
    public double Score(RawExtractedField field, IReadOnlyList<OcrBlock> blocks)
    {
        if (field.RawValue is null) return 0.0;

        // Tesseract word-level confidence:
        //   • Keyword/Position: average confidence of blocks overlapping the bounding box.
        //   • Regex: no bounding box, so find blocks whose text appears in (or contains) the
        //     matched value and use their average confidence.
        //   • Fallback: 0.5 (neutral) when nothing can be matched.
        double tesseractConf = 0.5;
        if (field.BoundingBox is not null && field.Page.HasValue)
        {
            var matchingBlocks = blocks
                .Where(b => b.Page == field.Page.Value && OverlapsWith(b.BoundingBox, field.BoundingBox))
                .ToList();
            if (matchingBlocks.Count > 0)
                tesseractConf = matchingBlocks.Average(b => (double)b.Confidence);
        }
        else if (field.ExtractionStrategy == "Regex")
        {
            var matchingBlocks = blocks
                .Where(b => !string.IsNullOrWhiteSpace(b.Text) &&
                            (field.RawValue.Contains(b.Text, StringComparison.OrdinalIgnoreCase) ||
                             b.Text.Contains(field.RawValue, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (matchingBlocks.Count > 0)
                tesseractConf = matchingBlocks.Average(b => (double)b.Confidence);
        }

        // Strategy-type reliability (how inherently trustworthy is the extraction method).
        double regexConf = field.ExtractionStrategy switch
        {
            "Regex"    => 1.0,
            "Keyword"  => 0.5,
            "Position" => 0.0,
            _          => 0.0
        };

        // Strategy-level confidence:
        //   • Regex  → 1.0  (pattern matched → full strategy confidence)
        //   • Keyword → distance-based 0.3–1.0 from FieldExtractor
        //   • Position/None → 0.0
        // Previously this was only applied for Keyword; using it for all strategies means
        // a successful Regex match contributes its full 0.30 weight instead of 0.
        double strategyConf = (double)field.StrategyConfidence;

        // Weighted composite: tesseract 40%, strategy type 30%, strategy confidence 30%.
        return Math.Clamp((tesseractConf * 0.40) + (regexConf * 0.30) + (strategyConf * 0.30), 0.0, 1.0);
    }

    private static bool OverlapsWith(OcrBoundingBox a, OcrBoundingBox b) =>
        a.X < b.X + b.Width && a.X + a.Width > b.X &&
        a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;
}
