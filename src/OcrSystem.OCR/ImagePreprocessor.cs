using System.Text;
using SkiaSharp;
using UglyToad.PdfPig;

namespace OcrSystem.OCR;

public interface IImagePreprocessor
{
    Task<IReadOnlyList<ProcessedPageImage>> PreprocessAsync(Stream fileStream, string mimeType, CancellationToken ct = default);

    // Claude-optimised preprocessing: no hard threshold, images resized to ≤ 1568 px.
    // For digital PDFs the pre-extracted text blocks are populated and no image is rendered.
    Task<IReadOnlyList<ProcessedPageImage>> PreprocessForClaudeAsync(Stream fileStream, string mimeType, CancellationToken ct = default);

    // PaddleOCR-optimised preprocessing: greyscale at 300 DPI, NO binary threshold.
    // PP-Structure needs the soft grey lines that form table borders; binarising at 128
    // turns those lines white (invisible) and table detection fails completely.
    Task<IReadOnlyList<ProcessedPageImage>> PreprocessForPaddleAsync(Stream fileStream, string mimeType, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the PDF stream contains any embedded raster images on any page.
    /// Used to decide whether to use the fast PdfPig text-extraction path or the
    /// slower Poppler full-render path (needed for hybrid PDFs with scanned letterheads).
    /// Stream position is restored after the check.
    /// </summary>
    Task<bool> PdfHasEmbeddedImagesAsync(Stream pdfStream, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the PDF stream contains any selectable (digital) text on any page.
    /// Used together with PdfHasEmbeddedImagesAsync to detect pure scanned PDFs where
    /// PdfPig cannot extract embedded images — those must go through the Poppler render path.
    /// Stream position is restored after the check.
    /// </summary>
    Task<bool> PdfHasSelectableTextAsync(Stream pdfStream, CancellationToken ct = default);

    /// <summary>
    /// For each hybrid page (has both selectable digital text AND embedded images),
    /// returns the pixel Y coordinate at which the first selectable word begins.
    /// Everything above that line is sent to PaddleOCR (letterhead/logo area);
    /// everything from that line down is handled by PdfPig text extraction.
    /// Only pages with both content types are included. Stream position is restored.
    /// </summary>
    Task<IReadOnlyDictionary<int, int>> GetHybridPageCropsAsync(
        Stream pdfStream, int dpi, CancellationToken ct = default);

    /// <summary>
    /// Returns true if the PDF's text layer uses a non-standard font glyph encoding
    /// (e.g. glyph codes offset by ±29 from Unicode) that causes PdfPig to extract
    /// garbled characters. When true, the pipeline should use Poppler image rendering
    /// instead of PdfPig text extraction — Poppler renders visually correct output
    /// regardless of the font's ToUnicode/Encoding map.
    /// Stream position is restored after the check.
    /// </summary>
    Task<bool> HasGarbledFontEncodingAsync(Stream pdfStream, CancellationToken ct = default);
}

// PreExtractedBlocks is populated for text-based PDFs so TesseractOcrEngine
// can skip the image-render → OCR round-trip and use PdfPig's text directly.
public record ProcessedPageImage(int PageNumber, byte[] ImageData, int Width, int Height, int Dpi,
    IReadOnlyList<OcrBlock>? PreExtractedBlocks = null);

public class ImagePreprocessor : IImagePreprocessor
{
    private const int TargetDpi = 300;

    // PDF magic bytes: %PDF (0x25 0x50 0x44 0x46)
    private static async Task<bool> IsPdfAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        var read = await stream.ReadAsync(header.AsMemory(0, 4), ct);
        return read == 4
            && header[0] == 0x25 && header[1] == 0x50
            && header[2] == 0x44 && header[3] == 0x46;
    }

    public async Task<IReadOnlyList<ProcessedPageImage>> PreprocessAsync(Stream fileStream, string mimeType, CancellationToken ct = default)
    {
        fileStream.Seek(0, SeekOrigin.Begin);

        // Detect PDF by magic bytes first — browsers/proxies sometimes send
        // application/octet-stream for PDFs, causing the MIME check to miss.
        var isPdf = mimeType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                    || await IsPdfAsync(fileStream, ct);

        fileStream.Seek(0, SeekOrigin.Begin);

        if (isPdf)
            return await ProcessPdfAsync(fileStream, ct);

        return [await ProcessImageAsync(fileStream, 1)];
    }

    private static async Task<IReadOnlyList<ProcessedPageImage>> ProcessPdfAsync(Stream pdfStream, CancellationToken ct)
    {
        var pages = new List<ProcessedPageImage>();
        using var doc = PdfDocument.Open(pdfStream);
        int pageNum = 1;
        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            // Convert page dimensions (PDF points) to pixels at target DPI
            var widthPx  = Math.Max((int)(page.Width  / 72.0 * TargetDpi), 100);
            var heightPx = Math.Max((int)(page.Height / 72.0 * TargetDpi), 100);

            // --- Direct text extraction (text-based PDFs) ---
            // PdfPig can read embedded text directly; no OCR needed for those.
            var directBlocks = new List<OcrBlock>();
            foreach (var word in page.GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;

                // PDF coordinate origin is bottom-left; convert to top-left for image space.
                var x = (int)(word.BoundingBox.Left   / page.Width  * widthPx);
                var y = (int)((1.0 - word.BoundingBox.Top / page.Height) * heightPx);
                var w = Math.Max((int)(word.BoundingBox.Width  / page.Width  * widthPx), 1);
                var h = Math.Max((int)(word.BoundingBox.Height / page.Height * heightPx), 1);

                directBlocks.Add(new OcrBlock(
                    pageNum,
                    word.Text.Trim(),
                    0.92f,
                    new OcrBoundingBox(x, y, w, h),
                    OcrBlockType.Word));
            }

            // --- Scanned PDF: extract embedded raster images for OCR ---
            // When PdfPig finds no text, the page is an image scan. Extract the
            // largest embedded image and preprocess it exactly like a standalone image.
            if (directBlocks.Count == 0)
            {
                var embeddedImages = page.GetImages().ToList();
                if (embeddedImages.Count > 0)
                {
                    var largest = embeddedImages
                        .OrderByDescending(img => img.WidthInSamples * img.HeightInSamples)
                        .First();

                    // Primary: PNG conversion (works for most embedded image formats).
                    if (largest.TryGetPng(out var pngBytes))
                    {
                        using var imgStream = new MemoryStream(pngBytes);
                        var preprocessed = await ProcessImageAsync(imgStream, pageNum++);
                        pages.Add(preprocessed);
                        continue;
                    }

                    // Fallback: try decoding the raw image bytes directly (e.g., JPEG images
                    // embedded in PDFs that PdfPig cannot convert to PNG).
                    var rawBytes = largest.RawBytes.ToArray();
                    if (rawBytes.Length > 0)
                    {
                        using var rawStream = new MemoryStream(rawBytes);
                        using var decoded = SKBitmap.Decode(rawStream);
                        if (decoded is not null)
                        {
                            rawStream.Seek(0, SeekOrigin.Begin);
                            var preprocessed = await ProcessImageAsync(rawStream, pageNum++);
                            pages.Add(preprocessed);
                            continue;
                        }
                    }
                }
            }

            // --- Apply font-encoding fix for shifted-character PDFs ---
            if (directBlocks.Count > 0)
                directBlocks = FixBlockEncoding(directBlocks);

            // --- Render image for the document viewer (text PDF) ---
            using var bitmap = new SKBitmap(widthPx, heightPx, SKColorType.Gray8, SKAlphaType.Opaque);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);

            if (directBlocks.Count > 0)
            {
                using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
                foreach (var word in page.GetWords())
                {
                    if (string.IsNullOrWhiteSpace(word.Text)) continue;
                    var x = (float)(word.BoundingBox.Left / page.Width * widthPx);
                    var y = (float)((1.0 - word.BoundingBox.Bottom / page.Height) * heightPx);
                    paint.TextSize = Math.Max((float)(word.BoundingBox.Height / page.Height * heightPx), 8f);
                    canvas.DrawText(word.Text, x, y, paint);
                }
            }

            using var image = SKImage.FromBitmap(bitmap);
            var imageData = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
            pages.Add(new ProcessedPageImage(
                pageNum++, imageData, widthPx, heightPx, TargetDpi,
                directBlocks.Count > 0 ? directBlocks : null));
        }
        return pages;
    }

    private static Task<ProcessedPageImage> ProcessImageAsync(Stream imageStream, int pageNumber)
    {
        using var original = SKBitmap.Decode(imageStream);
        if (original is null)
            throw new InvalidOperationException("Could not decode image");

        // Convert to grayscale
        using var grayscale = new SKBitmap(original.Width, original.Height, SKColorType.Gray8, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(grayscale))
        {
            using var paint = new SKPaint
            {
                ColorFilter = SKColorFilter.CreateColorMatrix(new float[]
                {
                    0.299f, 0.587f, 0.114f, 0, 0,
                    0.299f, 0.587f, 0.114f, 0, 0,
                    0.299f, 0.587f, 0.114f, 0, 0,
                    0,      0,      0,      1, 0
                })
            };
            canvas.DrawBitmap(original, 0, 0, paint);
        }

        // Apply adaptive threshold
        using var thresholded = ApplyThreshold(grayscale, 128);
        using var image = SKImage.FromBitmap(thresholded);
        var imageData = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();

        return Task.FromResult(new ProcessedPageImage(pageNumber, imageData, thresholded.Width, thresholded.Height, TargetDpi));
    }

    private static SKBitmap ApplyThreshold(SKBitmap source, byte threshold)
    {
        var result = new SKBitmap(source.Width, source.Height, SKColorType.Gray8, SKAlphaType.Opaque);
        for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
            {
                var pixel = source.GetPixel(x, y);
                var gray = (byte)((pixel.Red + pixel.Green + pixel.Blue) / 3);
                result.SetPixel(x, y, gray < threshold ? SKColors.Black : SKColors.White);
            }
        return result;
    }

    // ── PDF font-encoding correction ──────────────────────────────────────────

    /// <summary>
    /// Detects the common PDF font encoding bug where PdfPig extracts characters
    /// shifted 29 positions below their true Unicode value because the font lacks
    /// a proper ToUnicode map (e.g. '$' was really 'A', '0' was 'M').
    /// Returns 29 when the pattern is detected, 0 otherwise.
    /// </summary>
    private static int DetectFontShiftOffset(IReadOnlyList<OcrBlock> blocks)
    {
        int totalPrintable   = 0;
        int alphaCount       = 0;
        int shiftCandidates  = 0;  // chars that become letters when +29 applied
        int abcProxies       = 0;  // ^, _, ` — represent A, B, C in -29 encoded PDFs

        foreach (var block in blocks)
        {
            foreach (char c in block.Text)
            {
                if (c is ' ' or '\t') continue;
                totalPrintable++;
                if (char.IsLetter(c)) alphaCount++;

                // Would adding 29 produce a letter (A–Z or a–z)?
                int shifted = c + 29;
                if (shifted is (>= 65 and <= 90) or (>= 97 and <= 122))
                    shiftCandidates++;

                // ^(94) _( 95) `(96) appear as word-forming characters only when the
                // font glyph index is offset by +29 relative to Unicode (i.e. A→^, B→_, C→`).
                // They are effectively absent in real natural-language business documents.
                if (c is '^' or '_' or '`')
                    abcProxies++;
            }
        }

        if (totalPrintable < 10) return 0;

        double alphaRatio  = (double)alphaCount      / totalPrintable;
        double shiftRatio  = (double)shiftCandidates / totalPrintable;
        double abcRatio    = (double)abcProxies      / totalPrintable;

        // +29 garbled: very few real letters but most chars become letters after +29 shift.
        if (alphaRatio < 0.30 && shiftRatio > 0.50)
            return 29;

        // -29 garbled: text looks mostly like letters (alphaRatio high) but ^, _, `
        // appear frequently inside words — a near-impossible pattern in real text.
        // These chars represent A, B, C from a font with glyph codes offset +29 above Unicode.
        if (alphaRatio > 0.50 && abcRatio > 0.03)
            return -29;

        return 0;
    }

    private static string ApplyCharacterShift(string text, int offset)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c is ' ' or '\t' or '\n' or '\r') { sb.Append(c); continue; }
            int shifted = c + offset;
            sb.Append(shifted is >= 32 and <= 126 ? (char)shifted : c);
        }
        return sb.ToString();
    }

    private static List<OcrBlock> FixBlockEncoding(List<OcrBlock> blocks)
    {
        int offset = DetectFontShiftOffset(blocks);
        if (offset == 0) return blocks;
        return blocks.Select(b => b with { Text = ApplyCharacterShift(b.Text, offset) }).ToList();
    }

    // ── Embedded-image detection ──────────────────────────────────────────────

    public Task<bool> PdfHasEmbeddedImagesAsync(Stream pdfStream, CancellationToken ct = default)
    {
        var origin = pdfStream.Position;
        try
        {
            pdfStream.Seek(0, SeekOrigin.Begin);
            using var doc = PdfDocument.Open(pdfStream);
            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                if (page.GetImages().Any())
                    return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
        finally
        {
            pdfStream.Seek(origin, SeekOrigin.Begin);
        }
    }

    public Task<bool> PdfHasSelectableTextAsync(Stream pdfStream, CancellationToken ct = default)
    {
        var origin = pdfStream.Position;
        try
        {
            pdfStream.Seek(0, SeekOrigin.Begin);
            using var doc = PdfDocument.Open(pdfStream);
            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                if (page.GetWords().Any())
                    return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
        finally
        {
            pdfStream.Seek(origin, SeekOrigin.Begin);
        }
    }

    public Task<bool> HasGarbledFontEncodingAsync(Stream pdfStream, CancellationToken ct = default)
    {
        var origin = pdfStream.Position;
        try
        {
            pdfStream.Seek(0, SeekOrigin.Begin);
            using var doc = PdfDocument.Open(pdfStream);
            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var words = page.GetWords()
                    .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                    .Select(w => w.Text.Trim())
                    .Where(t => t.Length > 0)
                    .ToList();

                if (words.Count == 0) continue;

                // Check 1: control characters in the text stream.
                // Characters below 32 (ESC, SOH, etc.) never appear in real business document
                // text — their presence is a near-certain sign of garbled glyph encoding
                // where PdfPig reads raw glyph codes instead of mapped Unicode.
                if (words.Any(w => w.Any(c => c < 32)))
                    return Task.FromResult(true);

                // Check 2: many words composed entirely of printable symbols
                // (e.g. "#$%&'", "!.", "-/") — impossible in real invoice/statement text.
                // A word is "symbolic" if none of its characters are letters, digits, or
                // common single-char punctuation (. , - / : ( ) ' @ + space).
                int symbolicWords = words.Count(w =>
                    w.Length >= 2 &&
                    w.All(c => !char.IsLetterOrDigit(c) &&
                                c is not ('.' or ',' or '-' or '/' or ':' or '(' or ')' or '\'' or '@' or '+' or ' ')));
                double symbolicRatio = (double)symbolicWords / words.Count;
                if (symbolicRatio > 0.30)
                    return Task.FromResult(true);

                // Check 3: original ±29-shift heuristic (handles documents where the
                // readable header inflates alphaRatio — run on body words only by
                // excluding words that are already valid English/Malay words).
                var blocks = words.Select(w => new OcrBlock(page.Number, w, 0.92f,
                        new OcrBoundingBox(0, 0, 1, 1), OcrBlockType.Word))
                    .ToList();
                if (DetectFontShiftOffset(blocks) != 0)
                    return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }
        finally
        {
            pdfStream.Seek(origin, SeekOrigin.Begin);
        }
    }

    public Task<IReadOnlyDictionary<int, int>> GetHybridPageCropsAsync(
        Stream pdfStream, int dpi, CancellationToken ct = default)
    {
        var origin = pdfStream.Position;
        try
        {
            pdfStream.Seek(0, SeekOrigin.Begin);
            using var doc = PdfDocument.Open(pdfStream);

            var result = new Dictionary<int, int>();
            int pageNum = 1;

            foreach (var page in doc.GetPages())
            {
                ct.ThrowIfCancellationRequested();

                var words  = page.GetWords().ToList();
                var images = page.GetImages().ToList();

                // Only hybrid pages: must have BOTH digital text AND embedded images.
                if (words.Count > 0 && images.Count > 0)
                {
                    double scale = dpi / 72.0;

                    // Use the TOP of the first selectable word as the split boundary.
                    // PDF Y-axis: 0 at bottom, page.Height at top.
                    // The visually topmost word has the LARGEST BoundingBox.Top value.
                    // Convert to pixel Y (origin top-left):
                    //   pixel_y = (page.Height - word.BoundingBox.Top) * scale
                    double maxPdfTop = words.Max(w => w.BoundingBox.Top);
                    int cropY = (int)((page.Height - maxPdfTop) * scale);

                    // Only apply if there is a meaningful image area above the first word.
                    if (cropY > 30)
                        result[pageNum] = cropY;
                }

                pageNum++;
            }

            return Task.FromResult<IReadOnlyDictionary<int, int>>(result);
        }
        finally
        {
            pdfStream.Seek(origin, SeekOrigin.Begin);
        }
    }

    // ── Claude-optimised preprocessing ────────────────────────────────────────

    public async Task<IReadOnlyList<ProcessedPageImage>> PreprocessForClaudeAsync(
        Stream fileStream, string mimeType, CancellationToken ct = default)
    {
        fileStream.Seek(0, SeekOrigin.Begin);
        var isPdf = mimeType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                    || await IsPdfAsync(fileStream, ct);
        fileStream.Seek(0, SeekOrigin.Begin);

        if (isPdf)
            return await ProcessPdfForClaudeAsync(fileStream, ct);

        return [await ProcessImageForClaudeAsync(fileStream, 1)];
    }

    // Same PdfPig text extraction as ProcessPdfAsync.
    // Digital pages get an empty image (Claude uses the pre-extracted text).
    // Scanned pages get Claude-optimised images (resized, no threshold).
    private static async Task<IReadOnlyList<ProcessedPageImage>> ProcessPdfForClaudeAsync(
        Stream pdfStream, CancellationToken ct)
    {
        var pages = new List<ProcessedPageImage>();
        using var doc = PdfDocument.Open(pdfStream);
        int pageNum = 1;

        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            var widthPx  = Math.Max((int)(page.Width  / 72.0 * TargetDpi), 100);
            var heightPx = Math.Max((int)(page.Height / 72.0 * TargetDpi), 100);

            // Extract embedded text (text-based PDFs)
            var directBlocks = new List<OcrBlock>();
            foreach (var word in page.GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;
                var x = (int)(word.BoundingBox.Left   / page.Width  * widthPx);
                var y = (int)((1.0 - word.BoundingBox.Top / page.Height) * heightPx);
                var w = Math.Max((int)(word.BoundingBox.Width  / page.Width  * widthPx), 1);
                var h = Math.Max((int)(word.BoundingBox.Height / page.Height * heightPx), 1);
                directBlocks.Add(new OcrBlock(
                    pageNum, word.Text.Trim(), 0.92f, new OcrBoundingBox(x, y, w, h), OcrBlockType.Word));
            }

            if (directBlocks.Count == 0)
            {
                // Scanned page — extract embedded raster image and preprocess for Claude
                var embeddedImages = page.GetImages().ToList();
                if (embeddedImages.Count > 0)
                {
                    var largest = embeddedImages
                        .OrderByDescending(img => img.WidthInSamples * img.HeightInSamples)
                        .First();

                    if (largest.TryGetPng(out var pngBytes))
                    {
                        using var imgStream = new MemoryStream(pngBytes);
                        pages.Add(await ProcessImageForClaudeAsync(imgStream, pageNum++));
                        continue;
                    }

                    var rawBytes = largest.RawBytes.ToArray();
                    if (rawBytes.Length > 0)
                    {
                        using var rawStream = new MemoryStream(rawBytes);
                        using var decoded = SKBitmap.Decode(rawStream);
                        if (decoded is not null)
                        {
                            rawStream.Seek(0, SeekOrigin.Begin);
                            pages.Add(await ProcessImageForClaudeAsync(rawStream, pageNum++));
                            continue;
                        }
                    }
                }
            }

            // --- Apply font-encoding fix for shifted-character PDFs ---
            if (directBlocks.Count > 0)
                directBlocks = FixBlockEncoding(directBlocks);

            // Digital page — store pre-extracted text; Claude does not need the image.
            pages.Add(new ProcessedPageImage(
                pageNum++, [], widthPx, heightPx, TargetDpi,
                directBlocks.Count > 0 ? directBlocks : null));
        }

        return pages;
    }

    // Resize to Claude's recommended max (1568 px on the longest side),
    // convert to grayscale — no hard threshold so Claude's vision handles contrast.
    private static Task<ProcessedPageImage> ProcessImageForClaudeAsync(Stream imageStream, int pageNumber)
    {
        using var original = SKBitmap.Decode(imageStream);
        if (original is null)
            throw new InvalidOperationException("Could not decode image");

        const int MaxDimension = 1568;
        int width  = original.Width;
        int height = original.Height;

        if (width > MaxDimension || height > MaxDimension)
        {
            var scale = Math.Min((double)MaxDimension / width, (double)MaxDimension / height);
            width  = (int)(width  * scale);
            height = (int)(height * scale);
        }

        using var grayscale = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(grayscale))
        {
            using var paint = new SKPaint
            {
                ColorFilter = SKColorFilter.CreateColorMatrix(new float[]
                {
                    0.299f, 0.587f, 0.114f, 0, 0,
                    0.299f, 0.587f, 0.114f, 0, 0,
                    0.299f, 0.587f, 0.114f, 0, 0,
                    0,      0,      0,      1, 0
                })
            };
            canvas.DrawBitmap(original, new SKRect(0, 0, width, height), paint);
        }

        using var image = SKImage.FromBitmap(grayscale);
        var imageData = image.Encode(SKEncodedImageFormat.Png, 90).ToArray();
        return Task.FromResult(new ProcessedPageImage(pageNumber, imageData, width, height, 96));
    }

    // ── PaddleOCR-optimised preprocessing ─────────────────────────────────────
    // Greyscale at 300 DPI — no binary threshold.
    // PP-Structure needs the soft grey lines that define table cell borders.
    // Binarising at 128 (like PreprocessAsync) turns those lines white, making
    // table detection completely fail.

    public async Task<IReadOnlyList<ProcessedPageImage>> PreprocessForPaddleAsync(
        Stream fileStream, string mimeType, CancellationToken ct = default)
    {
        fileStream.Seek(0, SeekOrigin.Begin);
        var isPdf = mimeType.Contains("pdf", StringComparison.OrdinalIgnoreCase)
                    || await IsPdfAsync(fileStream, ct);
        fileStream.Seek(0, SeekOrigin.Begin);

        if (isPdf)
            return await ProcessPdfForPaddleAsync(fileStream, ct);

        return [await ProcessImageForPaddleAsync(fileStream, 1)];
    }

    private static async Task<IReadOnlyList<ProcessedPageImage>> ProcessPdfForPaddleAsync(
        Stream pdfStream, CancellationToken ct)
    {
        var pages = new List<ProcessedPageImage>();
        using var doc = PdfDocument.Open(pdfStream);
        int pageNum = 1;

        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            var widthPx  = Math.Max((int)(page.Width  / 72.0 * TargetDpi), 100);
            var heightPx = Math.Max((int)(page.Height / 72.0 * TargetDpi), 100);

            // Extract embedded text (text-based PDFs) — same as other paths.
            var directBlocks = new List<OcrBlock>();
            foreach (var word in page.GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;
                var x = (int)(word.BoundingBox.Left   / page.Width  * widthPx);
                var y = (int)((1.0 - word.BoundingBox.Top / page.Height) * heightPx);
                var w = Math.Max((int)(word.BoundingBox.Width  / page.Width  * widthPx), 1);
                var h = Math.Max((int)(word.BoundingBox.Height / page.Height * heightPx), 1);
                directBlocks.Add(new OcrBlock(
                    pageNum, word.Text.Trim(), 0.92f,
                    new OcrBoundingBox(x, y, w, h), OcrBlockType.Word));
            }

            if (directBlocks.Count == 0)
            {
                // Scanned page — extract embedded raster and send to PaddleOCR.
                var embeddedImages = page.GetImages().ToList();
                if (embeddedImages.Count > 0)
                {
                    var largest = embeddedImages
                        .OrderByDescending(img => img.WidthInSamples * img.HeightInSamples)
                        .First();

                    if (largest.TryGetPng(out var pngBytes))
                    {
                        using var imgStream = new MemoryStream(pngBytes);
                        pages.Add(await ProcessImageForPaddleAsync(imgStream, pageNum++));
                        continue;
                    }
                    var rawBytes = largest.RawBytes.ToArray();
                    if (rawBytes.Length > 0)
                    {
                        // SKBitmap.Decode disposes the stream internally, so we only use
                        // it to validate the bytes, then open a fresh stream for processing.
                        bool isValidImage;
                        using (var checkStream = new MemoryStream(rawBytes))
                        using (var decoded = SKBitmap.Decode(checkStream))
                            isValidImage = decoded is not null;

                        if (isValidImage)
                        {
                            using var freshStream = new MemoryStream(rawBytes);
                            pages.Add(await ProcessImageForPaddleAsync(freshStream, pageNum++));
                            continue;
                        }
                    }
                }
            }

            // --- Apply font-encoding fix for shifted-character PDFs ---
            if (directBlocks.Count > 0)
                directBlocks = FixBlockEncoding(directBlocks);

            // For hybrid pages (digital text body + embedded raster letterhead),
            // render the embedded images onto a canvas so PaddleOCR can read
            // the non-selectable header area. Pure digital pages get no image.
            byte[] hybridImageData = [];
            if (directBlocks.Count > 0)
            {
                var embeddedImages = page.GetImages().ToList();
                if (embeddedImages.Count > 0)
                {
                    using var bitmap = new SKBitmap(widthPx, heightPx, SKColorType.Gray8, SKAlphaType.Opaque);
                    using var canvas = new SKCanvas(bitmap);
                    canvas.Clear(SKColors.White);
                    foreach (var img in embeddedImages)
                    {
                        if (!img.TryGetPng(out var pngBytes)) continue;
                        using var imgBitmap = SKBitmap.Decode(pngBytes);
                        if (imgBitmap is null) continue;
                        // PDF origin is bottom-left; convert to top-left pixel coords.
                        var ix = (float)(img.Bounds.Left   / page.Width  * widthPx);
                        var iy = (float)((1.0 - img.Bounds.Top / page.Height) * heightPx);
                        var iw = (float)(img.Bounds.Width  / page.Width  * widthPx);
                        var ih = (float)(img.Bounds.Height / page.Height * heightPx);
                        canvas.DrawBitmap(imgBitmap, new SKRect(ix, iy, ix + iw, iy + ih));
                    }
                    using var skImg = SKImage.FromBitmap(bitmap);
                    hybridImageData = skImg.Encode(SKEncodedImageFormat.Png, 100).ToArray();
                }
            }

            pages.Add(new ProcessedPageImage(
                pageNum++, hybridImageData, widthPx, heightPx, TargetDpi,
                directBlocks.Count > 0 ? directBlocks : null));
        }

        return pages;
    }

    // Greyscale at 300 DPI, no threshold — preserves soft grey lines.
    private static Task<ProcessedPageImage> ProcessImageForPaddleAsync(
        Stream imageStream, int pageNumber)
    {
        using var original = SKBitmap.Decode(imageStream);
        if (original is null)
            throw new InvalidOperationException("Could not decode image");

        // Upscale small images to at least 300 DPI equivalent (2480 px wide for A4).
        // PaddleOCR text detection works best at ≥ 1200 px on the shorter side.
        const int MinShortSide = 1200;
        int width  = original.Width;
        int height = original.Height;
        int shortSide = Math.Min(width, height);
        if (shortSide < MinShortSide)
        {
            var scale = (double)MinShortSide / shortSide;
            width  = (int)(width  * scale);
            height = (int)(height * scale);
        }

        // Greyscale — soft values preserved (no binarisation).
        using var grayscale = new SKBitmap(width, height, SKColorType.Gray8, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(grayscale))
        {
            using var paint = new SKPaint
            {
                ColorFilter = SKColorFilter.CreateColorMatrix(new float[]
                {
                    0.299f, 0.587f, 0.114f, 0, 0,
                    0.299f, 0.587f, 0.114f, 0, 0,
                    0.299f, 0.587f, 0.114f, 0, 0,
                    0,      0,      0,      1, 0
                })
            };
            canvas.DrawBitmap(original, new SKRect(0, 0, width, height), paint);
        }

        using var image = SKImage.FromBitmap(grayscale);
        // Quality 100 — lossless PNG so no JPEG artefacts blur the text/lines.
        var imageData = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        return Task.FromResult(new ProcessedPageImage(pageNumber, imageData, width, height, TargetDpi));
    }
}
