import type { FormSpec, FormSpecField } from "./formSpec";
import { invoiceKinds } from "./formSpec";

/**
 * The review form of an invoice or credit note when the API sends no definition (a tenant without `configurableTypes`,
 * whose documents are version 1 trees of the coded `Invoice` record): the form the portal always showed, written as a
 * form spec so the same renderer draws it. It has no labels, so every label comes from the UI language (`fields.*`),
 * and the sections, fields, line columns and their order are the ones of the earlier `headerSections`/`lineColumns`.
 */
const field = (key: string, kind: string, section: string, extra: Partial<FormSpecField> = {}): FormSpecField => ({ path: key, key, kind, section, ...extra });

const line = (key: string, kind: string, extra: Partial<FormSpecField> = {}): FormSpecField => ({ path: `lines[*].${key}`, key, kind, ...extra });

export const legacyInvoiceFormSpec: FormSpec = {
  formatVersion: 1,
  key: "Invoice",
  version: 1,
  sections: [{ id: "document" }, { id: "supplier" }, { id: "customer" }, { id: "totals" }, { id: "payment" }, { id: "notes" }, { id: "lines" }],
  fields: [
    field("kind", "enum", "document", { options: invoiceKinds.map((code) => ({ code })) }),
    field("invoiceNumber", "identifier", "document"),
    field("issueDate", "date", "document"),
    field("deliveryDate", "date", "document"),
    field("dueDate", "date", "document"),
    field("servicePeriod", "period", "document"),
    field("currency", "string", "document"),
    field("orderReferences", "identifier", "document", { list: true }),
    field("deliveryNoteReferences", "identifier", "document", { list: true }),
    // A credit note's reference to the invoice it corrects: a plain invoice hides it unless it carries one anyway.
    field("originalInvoiceReference", "identifier", "document", { visibleWhen: { path: "kind", equals: "CreditNote" } }),
    field("supplier", "party", "supplier"),
    field("customer", "party", "customer"),
    field("subtotalNet", "money", "totals"),
    field("totalTax", "money", "totals"),
    field("totalGross", "money", "totals"),
    field("amountDue", "money", "totals"),
    field("rounding", "money", "totals"),
    field("paymentTerms", "paymentTerms", "payment"),
    field("notes", "text", "notes"),
    field("lines", "array", "lines", {
      children: [
        line("lineNumber", "integer", { provenance: false, readOnly: true }),
        line("unitRaw", "string", { provenance: false, readOnly: true }),
        // What the line arithmetic checks (quantity × unit price → line net) first, the optional columns after it.
        line("description", "string", { alwaysShown: true }),
        line("quantity", "decimal", { alwaysShown: true }),
        line("unit", "unit", { alwaysShown: true }),
        line("unitPrice", "money", { alwaysShown: true }),
        line("lineNet", "money", { alwaysShown: true }),
        line("taxRate", "percent", { alwaysShown: true }),
        line("supplierSku", "string"),
        line("discountPercent", "percent"),
        line("discountAmount", "money"),
        line("lineGross", "money"),
      ],
    }),
  ],
};

/** The built-in form of a legacy type without a definition from the API: the invoice family's, or none. */
export function legacyFormSpec(type: string | null | undefined): FormSpec | null {
  return type === "Invoice" || type === "CreditNote" ? legacyInvoiceFormSpec : null;
}
