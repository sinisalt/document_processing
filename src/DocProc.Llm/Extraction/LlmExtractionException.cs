namespace DocProc.Llm.Extraction;

/// <summary>
/// The provider answered, but the answer is unusable: truncated twice in a row, or JSON that does not deserialise
/// into the contract. Unlike <see cref="LlmException"/> this is not a provider or configuration fault, so the
/// pipeline treats it as a failed stage attempt rather than a tenant-blocking condition.
/// </summary>
public sealed class LlmExtractionException(string message, Exception? inner = null) : Exception(message, inner)
{
    public LlmExtractionException()
        : this("The LLM answer could not be used.")
    {
    }

    public LlmExtractionException(string message)
        : this(message, null)
    {
    }
}
