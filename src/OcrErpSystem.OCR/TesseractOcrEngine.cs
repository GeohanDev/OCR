using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tesseract;

namespace OcrErpSystem.OCR;

public interface ITesseractOcrEngine
{
    Task<TesseractOutput> RecognizeAsync(IReadOnlyList<ProcessedPageImage> pages, CancellationToken ct = default);
}

public record TesseractOutput(
    string FullText,
    IReadOnlyList<OcrBlock> Blocks,
    double OverallConfidence,
    string EngineVersion);

public record OcrBlock(
    int Page,
    string Text,
    float Confidence,
    OcrBoundingBox BoundingBox,
    OcrBlockType BlockType);

public record OcrBoundingBox(int X, int Y, int Width, int Height);

public enum OcrBlockType { Word, Line, Paragraph, Block }

public class TesseractOcrEngine : ITesseractOcrEngine
{
    private readonly string _tessDataPath;
    private readonly string _language;
    private readonly ILogger<TesseractOcrEngine> _logger;

    public TesseractOcrEngine(IConfiguration config, ILogger<TesseractOcrEngine> logger)
    {
        _tessDataPath = config["Tesseract:DataPath"] ?? Path.Combine(AppContext.BaseDirectory, "tessdata");
        _language = config["Tesseract:Language"] ?? "eng";
        _logger = logger;
    }

    public async Task<TesseractOutput> RecognizeAsync(IReadOnlyList<ProcessedPageImage> pages, CancellationToken ct = default)
    {
        var allBlocks = new List<OcrBlock>();
        var fullTextBuilder = new System.Text.StringBuilder();
        var confidences = new List<double>();

        // Only create a Tesseract engine if any page needs image-based OCR
        var needsTesseract = pages.Any(p => p.PreExtractedBlocks is null or { Count: 0 });
        TesseractEngine? engine = needsTesseract
            ? new TesseractEngine(_tessDataPath, _language, EngineMode.LstmOnly)
            : null;

        try
        {
            if (engine is not null)
            {
                // Improve recognition: tell Tesseract the image DPI and preserve spacing
                engine.SetVariable("user_defined_dpi", "300");
                engine.SetVariable("preserve_interword_spaces", "1");
            }

            foreach (var page in pages)
            {
                ct.ThrowIfCancellationRequested();

                // --- Fast path: text PDF already extracted by PdfPig ---
                if (page.PreExtractedBlocks is { Count: > 0 })
                {
                    allBlocks.AddRange(page.PreExtractedBlocks);
                    fullTextBuilder.AppendLine(
                        string.Join(" ", page.PreExtractedBlocks.Select(b => b.Text)));
                    confidences.Add(0.92);
                    continue;
                }

                // --- Slow path: scanned document or image — run Tesseract ---
                using var pix = Pix.LoadFromMemory(page.ImageData);
                var (pageText, meanConf, pageBlocks) = ProcessPageOcr(engine!, pix, page.PageNumber, PageSegMode.Auto);

                // Auto mode can fail on tightly-packed or structured layouts; retry with
                // SparseText which handles sparse/tabular content better.
                if (meanConf < 0.3 && pageBlocks.Count == 0)
                {
                    _logger.LogDebug("Page {P}: Auto PSM yielded no blocks (conf={C:F2}), retrying with SparseText",
                        page.PageNumber, meanConf);
                    (pageText, meanConf, pageBlocks) = ProcessPageOcr(engine!, pix, page.PageNumber, PageSegMode.SparseText);
                }

                fullTextBuilder.AppendLine(pageText);
                confidences.Add(meanConf);
                allBlocks.AddRange(pageBlocks);
            }
        }
        finally
        {
            engine?.Dispose();
        }

        await Task.CompletedTask;
        var overallConf = confidences.Count > 0 ? confidences.Average() : 0.0;
        _logger.LogDebug("OCR processed {PageCount} pages, confidence={Conf:F2}, blocks={Blocks}",
            pages.Count, overallConf, allBlocks.Count);

        return new TesseractOutput(fullTextBuilder.ToString(), allBlocks, overallConf, "5.2.0");
    }

    /// <summary>
    /// Runs Tesseract on a single <paramref name="pix"/> with the given <paramref name="mode"/>
    /// and returns the page text, mean confidence, and word-level blocks.
    /// </summary>
    private static (string Text, double MeanConf, List<OcrBlock> Blocks) ProcessPageOcr(
        TesseractEngine engine, Pix pix, int pageNumber, PageSegMode mode)
    {
        using var tesPage = engine.Process(pix, mode);
        var text = tesPage.GetText();
        var meanConf = tesPage.GetMeanConfidence();
        var blocks = new List<OcrBlock>();

        using var wordIter = tesPage.GetIterator();
        wordIter.Begin();
        do
        {
            if (wordIter.TryGetBoundingBox(PageIteratorLevel.Word, out var bbox))
            {
                var wordText = wordIter.GetText(PageIteratorLevel.Word);
                var conf = wordIter.GetConfidence(PageIteratorLevel.Word);
                if (!string.IsNullOrWhiteSpace(wordText))
                    blocks.Add(new OcrBlock(
                        pageNumber,
                        wordText.Trim(),
                        conf / 100f,
                        new OcrBoundingBox(bbox.X1, bbox.Y1, bbox.Width, bbox.Height),
                        OcrBlockType.Word));
            }
        } while (wordIter.Next(PageIteratorLevel.Word));

        return (text, meanConf, blocks);
    }
}
