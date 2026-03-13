using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OcrSystem.OCR;

// ── Public contract ───────────────────────────────────────────────────────────

public interface IPaddleOcrEngine
{
    bool IsConfigured { get; }
    Task<PaddleOcrOutput> RecognizeAsync(IReadOnlyList<ProcessedPageImage> pages, CancellationToken ct = default);
}

public record PaddleOcrOutput(
    string FullText,
    IReadOnlyList<OcrBlock> Blocks,
    IReadOnlyList<PaddleTable> Tables,
    double OverallConfidence,
    string EngineVersion);

/// <summary>A table detected by PP-StructureV3, expressed as HTML.</summary>
public record PaddleTable(string Html, int Page, int X, int Y, int Width, int Height);

// ── Implementation ────────────────────────────────────────────────────────────

public class PaddleOcrEngine : IPaddleOcrEngine
{
    private readonly HttpClient _http;
    private readonly ILogger<PaddleOcrEngine> _logger;

    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    public PaddleOcrEngine(HttpClient http, IConfiguration config, ILogger<PaddleOcrEngine> logger)
    {
        _http   = http;
        _logger = logger;

        var baseUrl = config["PaddleOcr:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(baseUrl))
            _http.BaseAddress = new Uri(baseUrl);
    }

    public bool IsConfigured => _http.BaseAddress is not null;

    public async Task<PaddleOcrOutput> RecognizeAsync(
        IReadOnlyList<ProcessedPageImage> pages, CancellationToken ct = default)
    {
        // For text-PDF pages PdfPig already extracted the text — no need to
        // send the image to PaddleOCR; map the pre-extracted blocks directly.
        var requestPages = new List<object>();
        var preExtractedByPage = new Dictionary<int, IReadOnlyList<OcrBlock>>();

        foreach (var p in pages)
        {
            if (p.PreExtractedBlocks is { Count: > 0 })
            {
                preExtractedByPage[p.PageNumber] = p.PreExtractedBlocks;
            }
            else if (p.ImageData.Length > 0)
            {
                requestPages.Add(new
                {
                    page         = p.PageNumber,
                    image_base64 = Convert.ToBase64String(p.ImageData)
                });
            }
        }

        var allBlocks  = new List<OcrBlock>();
        var allTables  = new List<PaddleTable>();
        var textParts  = new List<string>();
        var confs      = new List<double>();

        // ── Fast path: text PDFs ───────────────────────────────────────────────
        // PdfPig returns words in PDF content-stream order which is NOT always
        // visual top-to-bottom.  Sort by Y then X, group into visual rows (bin
        // height = 8 px) and join rows with newlines so the assembled text
        // preserves document structure — vendor name at top, table rows in order.
        foreach (var (pageNum, blocks) in preExtractedByPage)
        {
            allBlocks.AddRange(blocks);
            var rows = blocks
                .OrderBy(b => b.BoundingBox.Y)
                .ThenBy(b => b.BoundingBox.X)
                .GroupBy(b => b.BoundingBox.Y / 8)
                .OrderBy(g => g.Key)
                .Select(g => string.Join("  ", g.Select(b => b.Text)));
            textParts.Add(string.Join("\n", rows));
            confs.Add(0.92);
        }

        // ── Slow path: scanned pages via PaddleOCR ─────────────────────────────
        if (requestPages.Count > 0)
        {
            var body    = JsonSerializer.Serialize(new { pages = requestPages });
            using var req = new HttpRequestMessage(HttpMethod.Post, "/ocr")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };

            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var json   = await resp.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<PaddleOcrResponse>(json, JsonOpts);

            foreach (var page in result?.Pages ?? [])
            {
                if (!string.IsNullOrWhiteSpace(page.FullText))
                    textParts.Add(page.FullText);

                foreach (var b in page.Blocks ?? [])
                {
                    var bbox = b.Bbox ?? [0, 0, 0, 0];
                    allBlocks.Add(new OcrBlock(
                        b.Page,
                        b.Text,
                        (float)b.Confidence,
                        new OcrBoundingBox(
                            bbox.Count > 0 ? bbox[0] : 0,
                            bbox.Count > 1 ? bbox[1] : 0,
                            bbox.Count > 2 ? bbox[2] : 0,
                            bbox.Count > 3 ? bbox[3] : 0),
                        OcrBlockType.Line));
                    confs.Add(b.Confidence);
                }

                foreach (var t in page.Tables ?? [])
                {
                    var bbox = t.Bbox ?? [0, 0, 0, 0];
                    allTables.Add(new PaddleTable(
                        t.Html ?? "",
                        page.Page,
                        bbox.Count > 0 ? bbox[0] : 0,
                        bbox.Count > 1 ? bbox[1] : 0,
                        bbox.Count > 2 ? bbox[2] : 0,
                        bbox.Count > 3 ? bbox[3] : 0));
                }
            }
        }

        var overallConf = confs.Count > 0 ? confs.Average() : 0.0;
        var fullText    = string.Join("\n\n", textParts);

        _logger.LogInformation(
            "PaddleOCR: {Pages} pages, {Blocks} blocks, {Tables} tables, conf={Conf:F2}",
            pages.Count, allBlocks.Count, allTables.Count, overallConf);

        return new PaddleOcrOutput(
            fullText, allBlocks, allTables, overallConf, "PaddleOCR/PP-OCRv5+PP-StructureV3");
    }

    // ── JSON response models ───────────────────────────────────────────────────

    private record PaddleOcrResponse(
        [property: JsonPropertyName("pages")] List<PaddlePageResult>? Pages);

    private record PaddlePageResult(
        [property: JsonPropertyName("page")]      int Page,
        [property: JsonPropertyName("full_text")] string? FullText,
        [property: JsonPropertyName("blocks")]    List<PaddleBlockResult>? Blocks,
        [property: JsonPropertyName("tables")]    List<PaddleTableResponse>? Tables);

    private record PaddleBlockResult(
        [property: JsonPropertyName("text")]       string Text,
        [property: JsonPropertyName("confidence")] double Confidence,
        [property: JsonPropertyName("bbox")]       List<int>? Bbox,
        [property: JsonPropertyName("page")]       int Page);

    private record PaddleTableResponse(
        [property: JsonPropertyName("html")] string? Html,
        [property: JsonPropertyName("bbox")] List<int>? Bbox);
}
