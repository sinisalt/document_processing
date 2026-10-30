namespace DocProc.Infrastructure.Blobs;

/// <summary>
/// The three blob containers of docs/01 "Blob": immutable raw documents, derived artefacts (page images, OCR
/// output, previews) and exports. The names must match the containers the Aspire AppHost creates in Azurite
/// (<c>src/DocProc.AppHost/AppHost.cs</c>) and the ones Bicep creates in Azure.
/// </summary>
public static class BlobContainers
{
    /// <summary>Original bytes exactly as received; WORM policy in Azure.</summary>
    public const string Raw = "raw";

    /// <summary>Everything the pipeline computes from a raw document.</summary>
    public const string Derived = "derived";

    /// <summary>Datasets and files produced for customers.</summary>
    public const string Exports = "exports";

    /// <summary>Every container a store must be able to reach; used to create them locally and in tests.</summary>
    public static IReadOnlyList<string> All { get; } = [Raw, Derived, Exports];
}
