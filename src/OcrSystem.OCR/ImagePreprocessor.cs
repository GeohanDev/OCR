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

            // Digital page — store pre-extracted text blocks; no image needed.
            pages.Add(new ProcessedPageImage(
                pageNum++, [], widthPx, heightPx, TargetDpi,
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
