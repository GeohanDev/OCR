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

        using var engine = new TesseractEngine(_tessDataPath, _language, EngineMode.Default);

        foreach (var page in pages)
        {
            ct.ThrowIfCancellationRequested();
            using var pix = Pix.LoadFromMemory(page.ImageData);
            using var tesPage = engine.Process(pix, PageSegMode.Auto);

            fullTextBuilder.AppendLine(tesPage.GetText());
            confidences.Add(tesPage.GetMeanConfidence());

            using var wordIter = tesPage.GetIterator();
            wordIter.Begin();
            do
            {
                if (wordIter.TryGetBoundingBox(PageIteratorLevel.Word, out var bbox))
                {
                    var text = wordIter.GetText(PageIteratorLevel.Word);
                    var conf = wordIter.GetConfidence(PageIteratorLevel.Word);
                    if (!string.IsNullOrWhiteSpace(text))
                        allBlocks.Add(new OcrBlock(
                            page.PageNumber,
                            text.Trim(),
                            conf / 100f,
                            new OcrBoundingBox(bbox.X1, bbox.Y1, bbox.Width, bbox.Height),
                            OcrBlockType.Word));
                }
            } while (wordIter.Next(PageIteratorLevel.Word));
        }

        await Task.CompletedTask;
        var overallConf = confidences.Count > 0 ? confidences.Average() : 0;
        _logger.LogDebug("Tesseract processed {PageCount} pages, confidence={Conf:F2}", pages.Count, overallConf);

        return new TesseractOutput(fullTextBuilder.ToString(), allBlocks, overallConf, "5.2.0");
    }
}
