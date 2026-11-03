namespace DocProc.Definitions;

/// <summary>
/// The bounds on the <c>DocumentFieldIndex</c> rows of one document (docs/11 §6.2). A row field
/// (<c>lines[*].supplierSku</c>) or a list a type marks <c>indexed</c> has one value per row, and a list may have up to
/// <see cref="DefinitionChecker.MaxItemsLimit"/> rows, so without them a document could write tens of thousands of
/// index rows, rewritten on every Extract, Validate and correction. Section 5.8 advises against indexing row fields;
/// publishing warns when a type does (<c>PublishChecks</c>).
/// </summary>
public static class FieldIndexLimits
{
    /// <summary>
    /// The most values of one field a document indexes: the first ones, in row order. A later row's value is neither
    /// a column in the inbox nor found by <c>?field.&lt;key&gt;=</c>.
    /// </summary>
    public const int MaxValuesPerField = 100;

    /// <summary>
    /// The most index rows of one document, all its fields together. Every indexed field's first value is indexed
    /// before any field's second (so the inbox column and the duplicate check, which read the first value, never
    /// lose one to another field's rows); the further values fill what is left, field by field in definition order.
    /// </summary>
    public const int MaxEntriesPerDocument = 2_000;
}
