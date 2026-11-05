using System.Globalization;
using System.Text;
using DocProc.Application.Abstractions;
using DocProc.Domain.Common;
using DocProc.Domain.Invoices;

namespace DocProc.Llm.Prompts;

/// <summary>
/// Renders the per-document part of the extraction prompt (docs/06 "Request design", the user turn): the tenant
/// context so the model can tell which party block is the customer, the supplier memory when stage 5 matched one,
/// and, when neither a PDF nor page images can be attached, the text layer itself as the fallback input.
/// </summary>
public static class ExtractionTaskBuilder
{
    /// <summary>Known SKUs are a reading aid, not a list to copy; a long catalogue is truncated.</summary>
    public const int MaxKnownSkus = 50;

    /// <summary>Lines of the last approved invoice shown in the memory block.</summary>
    public const int MaxMemoryLines = 10;

    public static string Build(TenantHints tenant, SupplierHints? supplier, DocumentInput document)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(document);

        var sb = new StringBuilder();
        sb.Append("# Task\n");
        sb.Append("Extract the attached document into the invoice_extraction schema.\n\n");

        AppendTenant(sb, tenant);
        AppendSupplier(sb, supplier);
        AppendDocument(sb, document);

        return sb.ToString().TrimEnd();
    }

    /// <summary>The longest list of paths a fill-missing task names (docs/11 §9.3); a longer one is cut with a note.</summary>
    public const int MaxFillPaths = 200;

    /// <summary>
    /// The task of a definition-driven extraction (docs/11 §9.3, <c>LlmDefinitionExtractor</c>): the same tenant, supplier
    /// and document blocks as the invoice task, the type's name and the schema to fill, the definition's guidance
    /// (tenant text, length-capped by the meta-schema) and, in <c>fill-missing</c> mode, the paths still to fill.
    /// </summary>
    public static string BuildForDefinition(TenantHints tenant, SupplierHints? supplier, DocumentInput document, string typeName, string schemaName, string? guidance, IReadOnlyList<string>? fillPaths)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);

        var sb = new StringBuilder();
        sb.Append("# Task\n");
        sb.Append("Extract the attached document (a ").Append(typeName).Append(") into the ").Append(schemaName).Append(" schema.\n\n");
        if (!string.IsNullOrWhiteSpace(guidance))
        {
            sb.Append("## Guidance for this document type\n").Append(guidance.Trim()).Append("\n\n");
        }

        if (fillPaths is { Count: > 0 })
        {
            sb.Append("## Fields to fill\n");
            sb.Append("Earlier steps already read the other fields; return null for every field not listed here.\n");
            foreach (var path in fillPaths.Take(MaxFillPaths))
            {
                sb.Append("- ").Append(path).Append('\n');
            }

            if (fillPaths.Count > MaxFillPaths)
            {
                sb.Append("- (").Append((fillPaths.Count - MaxFillPaths).ToString(CultureInfo.InvariantCulture)).Append(" more)\n");
            }

            sb.Append('\n');
        }

        AppendTenant(sb, tenant);
        AppendSupplier(sb, supplier);
        AppendDocument(sb, document);

        return sb.ToString().TrimEnd();
    }

    private static void AppendTenant(StringBuilder sb, TenantHints tenant)
    {
        sb.Append("## Tenant (the expected customer)\n");
        sb.Append("Legal name: ").Append(tenant.LegalName).Append('\n');
        if (!string.IsNullOrWhiteSpace(tenant.VatId))
        {
            sb.Append("VAT id: ").Append(tenant.VatId).Append('\n');
        }

        sb.Append("Use this only to decide which printed party block is the customer. Do not copy it into the output unless it is printed on the document.\n\n");
    }

    private static void AppendSupplier(StringBuilder sb, SupplierHints? supplier)
    {
        if (supplier is null)
        {
            return;
        }

        sb.Append("## Supplier memory\n");
        sb.Append("Matched supplier: ").Append(supplier.Name);
        if (!string.IsNullOrWhiteSpace(supplier.VatId))
        {
            sb.Append(" (VAT id ").Append(supplier.VatId).Append(')');
        }

        sb.Append('\n');

        if (supplier.KnownSkus.Count > 0)
        {
            var shown = supplier.KnownSkus.Take(MaxKnownSkus).ToList();
            sb.Append(CultureInfo.InvariantCulture, $"Known SKUs ({shown.Count} of {supplier.KnownSkus.Count}): ");
            sb.Append(string.Join(", ", shown)).Append('\n');
        }

        if (supplier.LastApprovedInvoice is { } last)
        {
            AppendLastApproved(sb, last);
        }

        sb.Append("These are hints for reading this supplier's layout. Values are still copied from this document only.\n\n");
    }

    private static void AppendLastApproved(StringBuilder sb, Invoice last)
    {
        sb.Append("Last approved invoice: ");
        var parts = new List<string>();
        if (last.InvoiceNumber.HasValue)
        {
            parts.Add($"number {last.InvoiceNumber.Value!.Verbatim}");
        }

        if (last.IssueDate.HasValue)
        {
            parts.Add($"issued {last.IssueDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        }

        if (last.TotalGross.HasValue)
        {
            parts.Add($"total {last.TotalGross.Value}");
        }

        parts.Add(FormattableString.Invariant($"{last.Lines.Count} lines"));
        sb.Append(string.Join(", ", parts)).Append('\n');

        foreach (var line in last.Lines.Take(MaxMemoryLines))
        {
            sb.Append("  - ");
            if (line.SupplierSku.HasValue)
            {
                sb.Append(line.SupplierSku.Value).Append(' ');
            }

            if (line.Description.HasValue)
            {
                sb.Append('"').Append(line.Description.Value).Append("\" ");
            }

            if (line.Quantity.HasValue)
            {
                sb.Append(line.Quantity.Value.ToString(CultureInfo.InvariantCulture)).Append(' ');
                // Only a unit the approved invoice actually carried; a missing unit (or the Other bucket without raw text) is not invented.
                var unit = line.UnitRaw ?? (line.Unit is { HasValue: true, Value: not UnitOfMeasure.Other } ? line.Unit.Value.ToString() : null);
                if (unit is not null)
                {
                    sb.Append(unit).Append(' ');
                }
            }

            if (line.UnitPrice.HasValue)
            {
                sb.Append("@ ").Append(line.UnitPrice.Value.Amount.ToString("0.00##", CultureInfo.InvariantCulture));
            }

            sb.Append('\n');
        }

        if (last.Lines.Count > MaxMemoryLines)
        {
            sb.Append(CultureInfo.InvariantCulture, $"  - ... and {last.Lines.Count - MaxMemoryLines} more lines\n");
        }
    }

    private static void AppendDocument(StringBuilder sb, DocumentInput document)
    {
        sb.Append("## Document\n");
        if (document.HasPdf)
        {
            sb.Append("Attached as a PDF");
            if (document.PageTexts.Count > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $" ({document.PageTexts.Count} pages)");
            }

            sb.Append(".\n");
            return;
        }

        if (document.HasImages)
        {
            sb.Append(CultureInfo.InvariantCulture, $"Attached as {document.PageImages.Count} page images");
            sb.Append(document.HasText ? " together with the text layer of each page. Prefer the text layer for exact spellings and digits; use the images for layout and anything the text layer lacks.\n" : ".\n");
            return;
        }

        // Fallback: nothing can be attached, so the text layer is the document.
        sb.Append("No rendered pages are available. The text layer below is the whole document; pages are numbered in order.\n");
        for (var i = 0; i < document.PageTexts.Count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<page number=\"{i + 1}\">\n{document.PageTexts[i]}\n</page>\n");
        }
    }
}
