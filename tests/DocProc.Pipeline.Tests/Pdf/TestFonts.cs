using PdfSharp.Fonts;

namespace DocProc.Pipeline.Tests.Pdf;

/// <summary>
/// PDFsharp's Core build has no platform font lookup on Linux, so the fixtures resolve every face to one TrueType
/// font found on the machine (DejaVu, Liberation or FreeSans on Linux; Arial on Windows and macOS). The font is
/// embedded as a subset, so PdfPig reads real glyph widths and positions back.
/// </summary>
internal sealed class TestFonts : IFontResolver
{
    private const string FaceName = "docproc-test-sans";

    private static readonly string[] Candidates =
    [
        "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        "/usr/share/fonts/truetype/freefont/FreeSans.ttf",
        "/usr/share/fonts/TTF/DejaVuSans.ttf",
        "/Library/Fonts/Arial.ttf",
        "/System/Library/Fonts/Supplemental/Arial.ttf",
        @"C:\Windows\Fonts\arial.ttf",
    ];

    private static readonly Lazy<byte[]> FontBytes = new(LoadFont);

    public static string FontPath { get; } = Candidates.FirstOrDefault(File.Exists)
        ?? throw new FileNotFoundException("No TrueType font found for the PDF fixtures; install fonts-dejavu-core or fonts-liberation.");

    public byte[]? GetFont(string faceName) => FontBytes.Value;

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic) => new(FaceName);

    private static byte[] LoadFont() => File.ReadAllBytes(FontPath);
}
