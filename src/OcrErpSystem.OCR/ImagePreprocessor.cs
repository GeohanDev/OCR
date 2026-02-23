using SkiaSharp;
using UglyToad.PdfPig;

namespace OcrErpSystem.OCR;

public interface IImagePreprocessor
{
    Task<IReadOnlyList<ProcessedPageImage>> PreprocessAsync(Stream fileStream, string mimeType, CancellationToken ct = default);
}

public record ProcessedPageImage(int PageNumber, byte[] ImageData, int Width, int Height, int Dpi);

public class ImagePreprocessor : IImagePreprocessor
{
    private const int TargetDpi = 300;

    public async Task<IReadOnlyList<ProcessedPageImage>> PreprocessAsync(Stream fileStream, string mimeType, CancellationToken ct = default)
    {
        fileStream.Seek(0, SeekOrigin.Begin);

        if (mimeType.Contains("pdf", StringComparison.OrdinalIgnoreCase))
            return await ProcessPdfAsync(fileStream, ct);

        return [await ProcessImageAsync(fileStream, 1)];
    }

    private static Task<IReadOnlyList<ProcessedPageImage>> ProcessPdfAsync(Stream pdfStream, CancellationToken ct)
    {
        var pages = new List<ProcessedPageImage>();
        using var doc = PdfDocument.Open(pdfStream);
        int pageNum = 1;
        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            // Convert page dimensions (points) to pixels at target DPI
            var widthPx = Math.Max((int)(page.Width / 72.0 * TargetDpi), 100);
            var heightPx = Math.Max((int)(page.Height / 72.0 * TargetDpi), 100);

            // Create a white page image (in production, use a PDF renderer like PDFium)
            using var bitmap = new SKBitmap(widthPx, heightPx, SKColorType.Gray8, SKAlphaType.Opaque);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.White);

            // Draw text blocks from PdfPig extraction for basic image representation
            using var paint = new SKPaint { Color = SKColors.Black, TextSize = 12 };
            foreach (var word in page.GetWords())
            {
                var x = (float)(word.BoundingBox.Left / page.Width * widthPx);
                var y = (float)((1 - word.BoundingBox.Bottom / page.Height) * heightPx);
                canvas.DrawText(word.Text, x, y, paint);
            }

            using var image = SKImage.FromBitmap(bitmap);
            var imageData = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
            pages.Add(new ProcessedPageImage(pageNum++, imageData, widthPx, heightPx, TargetDpi));
        }
        return Task.FromResult<IReadOnlyList<ProcessedPageImage>>(pages);
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
}
