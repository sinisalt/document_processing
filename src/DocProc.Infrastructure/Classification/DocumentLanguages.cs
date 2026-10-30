namespace DocProc.Infrastructure.Classification;

/// <summary>ISO 639-1 codes the keyword classifier can report in <see cref="Application.Abstractions.ClassificationOutcome.Language"/>.</summary>
public static class DocumentLanguages
{
    public const string German = "de";
    public const string English = "en";
    public const string Croatian = "hr";
    public const string Italian = "it";
    public const string French = "fr";

    public static readonly IReadOnlyList<string> All = [German, English, Croatian, Italian, French];
}
