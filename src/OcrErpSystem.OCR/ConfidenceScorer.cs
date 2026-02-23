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

        // Tesseract confidence from blocks in bounding box
        double tesseractConf = 0.5; // neutral default when no bounding box
        if (field.BoundingBox is not null && field.Page.HasValue)
        {
            var matchingBlocks = blocks
                .Where(b => b.Page == field.Page.Value && OverlapsWith(b.BoundingBox, field.BoundingBox))
                .ToList();
            if (matchingBlocks.Count > 0)
                tesseractConf = matchingBlocks.Average(b => (double)b.Confidence);
        }

        // Strategy-based confidence weights
        double regexConf = field.ExtractionStrategy switch
        {
            "Regex" => 1.0,
            "Keyword" => 0.5,
            "Position" => 0.0,
            _ => 0.0
        };

        double keywordConf = field.ExtractionStrategy == "Keyword" ? (double)field.StrategyConfidence : 0.0;

        // Weighted composite: tesseract 40%, regex strategy 30%, keyword proximity 30%
        return Math.Clamp((tesseractConf * 0.40) + (regexConf * 0.30) + (keywordConf * 0.30), 0.0, 1.0);
    }

    private static bool OverlapsWith(OcrBoundingBox a, OcrBoundingBox b) =>
        a.X < b.X + b.Width && a.X + a.Width > b.X &&
        a.Y < b.Y + b.Height && a.Y + a.Height > b.Y;
}
