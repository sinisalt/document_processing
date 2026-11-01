namespace DocProc.Domain.Fields;

/// <summary>How a field value came to be, or why it is absent. Mirrors docs/02-data-model.md §3.</summary>
public enum FieldStatus
{
    /// <summary>Read from the document by any extraction path.</summary>
    Extracted,

    /// <summary>Not printed on the document; derived (for example a due date from payment terms, or the tenant default currency).</summary>
    Inferred,

    /// <summary>Not present on the document.</summary>
    Missing,

    /// <summary>Present on the document but could not be read (poor scan, damaged region).</summary>
    Unreadable,

    /// <summary>Two sources disagree and no resolution was made yet.</summary>
    Conflict,

    /// <summary>Set by a human reviewer; the previous value is kept in the history.</summary>
    Corrected,
}

/// <summary>Which mechanism produced a value.</summary>
public enum SourceMethod
{
    StructuredXml,
    Profile,
    Llm,
    Ocr,
    Human,
}
