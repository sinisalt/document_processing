using DocProc.Application.Configuration;

namespace DocProc.Application.Documents;

/// <summary>
/// Which documents are stored and edited by their definition (docs/11 §5.6, <c>CanonicalSchemaVersion</c> 2) rather than
/// by the coded records (version 1). The switch <c>configurableTypes</c> (docs/11 §3) gates it, so a tenant that is not
/// on the feature stores every document exactly as before.
/// </summary>
public static class CanonicalStorage
{
    /// <summary>
    /// True when <paramref name="configurable"/> is on and the type has a compiled field tree, and either the type has
    /// no coded record behind it (anything but the adopted invoice family: purchase orders, delivery notes, price lists,
    /// the tenant's own types) or the tenant copied or created it (its definition may add fields the record does not
    /// have). An adopted invoice or credit note stays a legacy tree, extracted and corrected as before.
    /// </summary>
    public static bool IsDefinitionDriven(EffectiveType? type, bool configurable)
    {
        if (!configurable || type?.Compiled is not { Descriptors.Fields.Count: > 0 })
        {
            return false;
        }

        var invoiceFamily = type.Definition.Key is "Invoice" or "CreditNote" || type.TemplateKey is "std.invoice" or "std.credit-note";
        return !invoiceFamily || type.Origin is DefinitionOrigin.Copied or DefinitionOrigin.Custom;
    }
}
