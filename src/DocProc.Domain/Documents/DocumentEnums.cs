namespace DocProc.Domain.Documents;

public enum TypeSource
{
    Rule,
    Llm,
    Human,

    /// <summary>Read from embedded structured data (an e-invoice XML), not from the page content.</summary>
    Structured,
}

public enum ExtractionMethod
{
    StructuredXml,
    Profile,
    Llm,
    Manual,
}

/// <summary>Who paid for an LLM call: the platform's key or the tenant's own (BYOK).</summary>
public enum KeyOwner
{
    Platform,
    Tenant,
}
