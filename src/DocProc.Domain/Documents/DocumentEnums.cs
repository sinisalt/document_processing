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

    /// <summary>
    /// Decided by an identification rule (docs/11 §8.4, §8.5): the supplier layout whose rule held set the type
    /// (<c>Documents.SupplierLayoutVersionId</c> names it).
    /// </summary>
    Identification,

    /// <summary>
    /// Proposed for a document held with <c>NEW_LAYOUT</c> (docs/11 §8.4 step 5): tenant rules, keywords or the LLM gave
    /// it, but it never counts as decided; the document cannot auto-approve and review confirms or changes it.
    /// </summary>
    Proposed,
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
