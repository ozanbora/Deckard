using Deckard.Core.Capture;
using Deckard.Perception.Imaging;
using Windows.Globalization;
using Windows.Media.Ocr;

namespace Deckard.Perception.Ocr;

/// <summary>A line of recognized text and where it sits in the image that was recognized.</summary>
public sealed record TextLine(string Text, PixelRect Bounds);

/// <summary>
/// Text recognition with the OCR engine built into Windows (Windows.Media.Ocr). It needs no model download,
/// only the OCR language pack, which English Windows installs by default.
/// </summary>
public sealed class TextRecognizer
{
    private readonly OcrEngine _engine;

    private TextRecognizer(OcrEngine engine) => _engine = engine;

    public string Language => _engine.RecognizerLanguage.LanguageTag;

    public static TextRecognizer Create(string languageTag = "en-US")
    {
        var language = new Language(languageTag);
        if (!OcrEngine.IsLanguageSupported(language))
        {
            var installed = string.Join(", ", OcrEngine.AvailableRecognizerLanguages.Select(l => l.LanguageTag));
            throw new NotSupportedException(
                $"No Windows OCR language pack for {languageTag} (installed: {installed}). Add it under Settings > Time & language > Language & region.");
        }
        return new TextRecognizer(OcrEngine.TryCreateFromLanguage(language));
    }

    public async Task<IReadOnlyList<TextLine>> RecognizeAsync(Frame image)
    {
        if (image.Width > OcrEngine.MaxImageDimension || image.Height > OcrEngine.MaxImageDimension)
            throw new ArgumentException($"Image is {image.Width}x{image.Height}; Windows OCR accepts at most {OcrEngine.MaxImageDimension} px per side.", nameof(image));

        using var bitmap = FrameFile.ToSoftwareBitmap(image);
        var result = await _engine.RecognizeAsync(bitmap);
        var lines = new List<TextLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            var bounds = default(PixelRect);
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                bounds = bounds.Union(new PixelRect((int)r.X, (int)r.Y, (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height)));
            }
            lines.Add(new TextLine(line.Text, bounds));
        }
        return lines;
    }
}
