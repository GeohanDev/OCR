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
    Task<PaddleOcrOutput> RecognizePdfAsync(
        byte[] pdfBytes,
        IReadOnlyDictionary<int, int>? pageCrops = null,
        CancellationToken ct = default);
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
                preExtractedByPage[p.PageNumber] = p.PreExtractedBlocks;

            // Always send image data when present — hybrid pages have both
            // pre-extracted body text AND an image of the raster letterhead.
            if (p.ImageData.Length > 0)
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
        var confs      = new List<double>();

        // Collect pre-extracted blocks; fullText is rebuilt after merging all sources.
        foreach (var (_, blocks) in preExtractedByPage)
        {
            allBlocks.AddRange(blocks);
            confs.Add(0.92);
        }

        // ── Slow path: scanned pages via PaddleOCR ────────────────────────────
        PaddleOcrResponse? paddleResp = null;
        if (requestPages.Count > 0)
        {
            var body = JsonSerializer.Serialize(new { pages = requestPages });
            using var req = new HttpRequestMessage(HttpMethod.Post, "/ocr")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            paddleResp = JsonSerializer.Deserialize<PaddleOcrResponse>(json, JsonOpts);

            foreach (var page in paddleResp?.Pages ?? [])
            {
                foreach (var b in page.Blocks ?? [])
                {
                    var bbox = b.Bbox ?? [0, 0, 0, 0];
                    allBlocks.Add(new OcrBlock(
                        b.Page, b.Text, (float)b.Confidence,
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
                        t.Html ?? "", page.Page,
                        bbox.Count > 0 ? bbox[0] : 0,
                        bbox.Count > 1 ? bbox[1] : 0,
                        bbox.Count > 2 ? bbox[2] : 0,
                        bbox.Count > 3 ? bbox[3] : 0));
                }
            }
        }

        var overallConf = confs.Count > 0 ? confs.Average() : 0.0;
        var fullText    = AssembleFullText(allBlocks);

        _logger.LogInformation(
            "PaddleOCR: {Pages} pages, {Blocks} blocks, {Tables} tables, conf={Conf:F2}",
            pages.Count, allBlocks.Count, allTables.Count, overallConf);

        return new PaddleOcrOutput(
            fullText, allBlocks, allTables, overallConf, "PaddleOCR/PP-OCRv5+PP-StructureV3");
    }

    public async Task<PaddleOcrOutput> RecognizePdfAsync(
        byte[] pdfBytes,
        IReadOnlyDictionary<int, int>? pageCrops = null,
        CancellationToken ct = default)
    {
        // Serialize page_crops with string keys to match Python's dict[str, int] model.
        object requestBody = pageCrops is { Count: > 0 }
            ? new
            {
                pdf_base64 = Convert.ToBase64String(pdfBytes),
                page_crops = pageCrops.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
            }
            : new { pdf_base64 = Convert.ToBase64String(pdfBytes) };

        var body = JsonSerializer.Serialize(requestBody);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/ocr-pdf")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json   = await resp.Content.ReadAsStringAsync(ct);
        var result = JsonSerializer.Deserialize<PaddleOcrResponse>(json, JsonOpts);

        var engineLabel = pageCrops is { Count: > 0 }
            ? "PaddleOCR/Poppler-Crop+PdfPig"
            : "PaddleOCR/Poppler-Render";
        return AssembleOutput(result, engineLabel);
    }

    private PaddleOcrOutput AssembleOutput(PaddleOcrResponse? result, string engineVersion)
    {
        var allBlocks = new List<OcrBlock>();
        var allTables = new List<PaddleTable>();
        var confs     = new List<double>();

        foreach (var page in result?.Pages ?? [])
        {
            foreach (var b in page.Blocks ?? [])
            {
                var bbox = b.Bbox ?? [0, 0, 0, 0];
                allBlocks.Add(new OcrBlock(
                    b.Page, b.Text, (float)b.Confidence,
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
                    t.Html ?? "", page.Page,
                    bbox.Count > 0 ? bbox[0] : 0,
                    bbox.Count > 1 ? bbox[1] : 0,
                    bbox.Count > 2 ? bbox[2] : 0,
                    bbox.Count > 3 ? bbox[3] : 0));
            }
        }

        var overallConf = confs.Count > 0 ? confs.Average() : 0.0;
        var fullText    = AssembleFullText(allBlocks);

        _logger.LogInformation(
            "PaddleOCR ({Engine}): {Pages} pages, {Blocks} blocks, conf={Conf:F2}",
            engineVersion, result?.Pages?.Count ?? 0, allBlocks.Count, overallConf);

        return new PaddleOcrOutput(fullText, allBlocks, allTables, overallConf, engineVersion);
    }

    // ── Text assembly ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reconstruct the document's spatial layout as plain text.
    /// Each block's X bounding-box coordinate is divided by <see cref="CharWidthPx"/>
    /// to get its character column, and the gap between consecutive blocks on the
    /// same row is filled with spaces (capped at <see cref="MaxGapChars"/>).
    /// This preserves table column alignment so Claude can tell which value belongs
    /// to which column (date vs reference vs amount) without needing bounding-box data.
    /// </summary>
    private static string AssembleFullText(IReadOnlyList<OcrBlock> blocks)
    {
        const int CharWidthPx = 10;  // pixels per character at 200 DPI (≈10 pt font)
        const int MaxGapChars = 60;  // cap runaway gaps from wide margins
        const int RowBinPx    = 15;  // same bin size as PaddleOCR service

        return string.Join("\n\n",
            blocks
                .GroupBy(b => b.Page)
                .OrderBy(g => g.Key)
                .Select(g =>
                    string.Join("\n",
                        g.GroupBy(b => b.BoundingBox.Y / RowBinPx)
                         .OrderBy(row => row.Key)
                         .Select(row =>
                         {
                             var sorted = row.OrderBy(b => b.BoundingBox.X).ToList();
                             var sb = new StringBuilder();
                             int currentEndCol = 0;
                             foreach (var block in sorted)
                             {
                                 int targetCol = block.BoundingBox.X / CharWidthPx;
                                 int gap = Math.Min(Math.Max(1, targetCol - currentEndCol), MaxGapChars);
                                 sb.Append(' ', gap);
                                 sb.Append(block.Text);
                                 currentEndCol = targetCol + block.Text.Length;
                             }
                             return sb.ToString().TrimStart();
                         }))));
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
