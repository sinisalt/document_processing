namespace DocProc.Application.Abstractions;

/// <summary>Why a document could not be processed; the pipeline records it as the reason of the failed stage (docs/03 "Document state machine").</summary>
public enum DocumentProcessingFailure
{
    /// <summary>The PDF is password protected and could not be opened.</summary>
    EncryptedPdf,

    /// <summary>The bytes are not a readable PDF (truncated, wrong format, broken cross-reference table).</summary>
    CorruptPdf,

    /// <summary>The PDF opened but one of its pages could not be processed.</summary>
    UnreadablePage,

    /// <summary>The processor cannot run on this platform (missing native libraries).</summary>
    UnsupportedPlatform,

    /// <summary>The file exceeds the configured size limit and is not loaded into memory.</summary>
    TooLarge,
}

/// <summary>
/// A stage could not process the document for a reason tied to the document itself rather than to a bug or an
/// outage. Handlers turn it into <c>Failed(reason)</c> (docs/03 "Document state machine") instead of retrying.
/// </summary>
public sealed class DocumentProcessingException : Exception
{
    public DocumentProcessingException()
        : this(DocumentProcessingFailure.CorruptPdf, "The document could not be processed.")
    {
    }

    public DocumentProcessingException(string message)
        : this(DocumentProcessingFailure.CorruptPdf, message)
    {
    }

    public DocumentProcessingException(string message, Exception? innerException)
        : this(DocumentProcessingFailure.CorruptPdf, message, innerException)
    {
    }

    public DocumentProcessingException(DocumentProcessingFailure failure, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
    }

    public DocumentProcessingFailure Failure { get; }
}
