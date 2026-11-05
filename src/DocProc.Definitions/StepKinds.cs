using System.Collections.Frozen;

namespace DocProc.Definitions;

/// <summary>
/// The built-in step kinds per phase (docs/11 §10.1). P0 has no step engine: the phases still run their fixed code,
/// and a template's step lists describe that code (for the invoice family: the e-invoice reader, then the LLM; the
/// coded rules, then the duplicate check). The step engine of P5 registers these kinds by key.
/// </summary>
public static class StepKinds
{
    public static readonly FrozenSet<string> Extract = new[]
    {
        "xml-einvoice", "supplier-mapping", "layout-prefill", "llm", "defaults", "code-list", "transform", "http-lookup",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static readonly FrozenSet<string> Validate = new[]
    {
        "validate.schema", "validate.rules", "validate.duplicate", "review-gate",
    }.ToFrozenSet(StringComparer.Ordinal);
}
