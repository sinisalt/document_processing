namespace DocProc.Domain.Documents;

public enum TypeSource
{
    Rule,
    Llm,
    Human,

    /// <summary>Read from embedded structured data (an e-invoice XML), not from the page content.</summary>
    Structured,

    /// <summary>
    /// Pinned by the supplier configuration (docs/11 §7.3): a source rule's <c>pinTypeKey</c>, the supplier's single
    /// allowed type under <c>fixDocumentType</c>, or the channel's default type. Split and Classify leave it.
    /// </summary>
    Supplier,
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
