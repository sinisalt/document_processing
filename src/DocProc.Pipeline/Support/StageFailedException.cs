namespace DocProc.Pipeline.Support;

/// <summary>
/// A stage decided the document can never be processed as it is (an unsupported format, an XML that is not an
/// e-invoice, no extraction path). Like <see cref="Application.Abstractions.DocumentProcessingException"/> the runner
/// turns it into <c>Failed(reason)</c> without a retry; unlike it, the reason is free text rather than a catalogue value.
/// </summary>
public sealed class StageFailedException : Exception
{
    public StageFailedException()
        : base("The stage could not process the document.")
    {
    }

    public StageFailedException(string message)
        : base(message)
    {
    }

    public StageFailedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
