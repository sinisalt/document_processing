import { getByPath, isFieldValue, joinPath, parsePath, setByPath, type FieldValue } from "@docproc/app-shell";

/**
 * The canonical invoice JSON as `GET /documents/{id}` returns it (`src/DocProc.Domain/Invoices/Invoice.cs`, camel-cased,
 * enums as names). Only the shapes the review form edits are typed here; everything else stays `unknown` and is passed
 * through untouched.
 */
export interface Money {
  amount: number;
  currency: string;
}

export interface DocumentIdentifier {
  verbatim: string;
  normalized?: string;
}

export interface Address {
  line1?: string | null;
  line2?: string | null;
  postalCode?: string | null;
  city?: string | null;
  region?: string | null;
  countryCode?: string | null;
}

export interface Party {
  name?: string | null;
  vatId?: string | null;
  taxId?: string | null;
  registrationNumber?: string | null;
  address?: Address | null;
  email?: string | null;
  phone?: string | null;
  iban?: string | null;
  bic?: string | null;
}

export interface PaymentTerms {
  dueDate?: string | null;
  discountPercent?: number | null;
  discountUntil?: string | null;
  paymentMeans?: string | null;
  paymentReference?: string | null;
  iban?: string | null;
  bic?: string | null;
  rawText?: string | null;
}

export interface DatePeriod {
  from: string;
  to: string;
}

export interface TaxBreakdownLine {
  rate: number;
  taxableAmount: Money;
  taxAmount: Money;
  category?: string | null;
}

export interface CanonicalLine {
  lineNumber: number;
  supplierSku?: FieldValue<string>;
  gtin?: FieldValue<string>;
  description?: FieldValue<string>;
  quantity?: FieldValue<number>;
  unit?: FieldValue<string>;
  unitRaw?: string | null;
  packSize?: FieldValue<string>;
  unitPrice?: FieldValue<Money>;
  discountPercent?: FieldValue<number>;
  discountAmount?: FieldValue<Money>;
  lineNet?: FieldValue<Money>;
  taxRate?: FieldValue<number>;
  lineGross?: FieldValue<Money>;
  depositAmount?: FieldValue<Money>;
  lotNumber?: FieldValue<string>;
  bestBefore?: FieldValue<string>;
}

export interface CanonicalInvoice {
  kind?: FieldValue<string>;
  invoiceNumber?: FieldValue<DocumentIdentifier>;
  issueDate?: FieldValue<string>;
  deliveryDate?: FieldValue<string>;
  servicePeriod?: FieldValue<DatePeriod>;
  dueDate?: FieldValue<string>;
  currency?: FieldValue<string>;
  supplier?: FieldValue<Party>;
  customer?: FieldValue<Party>;
  orderReferences?: FieldValue<DocumentIdentifier>[];
  deliveryNoteReferences?: FieldValue<DocumentIdentifier>[];
  originalInvoiceReference?: FieldValue<DocumentIdentifier>;
  subtotalNet?: FieldValue<Money>;
  taxBreakdown?: TaxBreakdownLine[];
  totalTax?: FieldValue<Money>;
  totalGross?: FieldValue<Money>;
  amountDue?: FieldValue<Money>;
  rounding?: FieldValue<Money>;
  paymentTerms?: FieldValue<PaymentTerms>;
  notes?: FieldValue<string>;
  lines?: CanonicalLine[];
}

export const invoiceKinds = ["Invoice", "CreditNote", "Proforma"] as const;
export const paymentMeans = ["BankTransfer", "DirectDebit", "Card", "Cash", "Other"] as const;
/**
 * `DocProc.Domain.Common.UnitOfMeasure`, in its order. `PATCH /fields` takes only these names for `lines[i].unit` (the
 * printed spellings such as "kg" or "Stk" are the extractor's business, kept in `unitRaw`), so the grid offers a select.
 */
export const unitsOfMeasure = ["Other", "Kilogram", "Gram", "Litre", "Millilitre", "Piece", "Box", "Case", "Crate", "Pallet", "Hour"] as const;

/** How a field is edited and which JSON shape `PATCH /fields` expects for it (docs/05 "shorthands"). */
export type FieldKind = "string" | "identifier" | "date" | "money" | "number" | "enum" | "text";

export interface FieldSpec {
  /** Canonical path (`invoiceNumber`, `supplier.address.city`, `lines[2].quantity`). */
  path: string;
  kind: FieldKind;
  /** The `FieldValue` this editable value lives in (`supplier` for `supplier.address.city`); equals `path` for whole fields. */
  fieldPath: string;
  /** Enum options for `kind: "enum"`. */
  options?: readonly string[];
}

const partySpecs = (root: string): FieldSpec[] =>
  [
    ["name", "string"],
    ["vatId", "string"],
    ["taxId", "string"],
    ["registrationNumber", "string"],
    ["email", "string"],
    ["phone", "string"],
    ["iban", "string"],
    ["bic", "string"],
    ["address.line1", "string"],
    ["address.line2", "string"],
    ["address.postalCode", "string"],
    ["address.city", "string"],
    ["address.region", "string"],
    ["address.countryCode", "string"],
  ].map(([name, kind]) => ({ path: `${root}.${name}`, kind: kind as FieldKind, fieldPath: root }));

/** Header fields of the review form in display order; sections group them visually. */
export interface FieldSection {
  key: "document" | "supplier" | "customer" | "totals" | "payment" | "notes";
  fields: FieldSpec[];
}

export function headerSections(canonical: CanonicalInvoice | null | undefined): FieldSection[] {
  const references: FieldSpec[] = [];
  (canonical?.orderReferences ?? []).forEach((_, i) => references.push({ path: `orderReferences[${i}]`, kind: "identifier", fieldPath: `orderReferences[${i}]` }));
  (canonical?.deliveryNoteReferences ?? []).forEach((_, i) =>
    references.push({ path: `deliveryNoteReferences[${i}]`, kind: "identifier", fieldPath: `deliveryNoteReferences[${i}]` }),
  );
  return [
    {
      key: "document",
      fields: [
        { path: "kind", kind: "enum", fieldPath: "kind", options: invoiceKinds },
        { path: "invoiceNumber", kind: "identifier", fieldPath: "invoiceNumber" },
        { path: "issueDate", kind: "date", fieldPath: "issueDate" },
        { path: "deliveryDate", kind: "date", fieldPath: "deliveryDate" },
        { path: "dueDate", kind: "date", fieldPath: "dueDate" },
        { path: "servicePeriod.from", kind: "date", fieldPath: "servicePeriod" },
        { path: "servicePeriod.to", kind: "date", fieldPath: "servicePeriod" },
        { path: "currency", kind: "string", fieldPath: "currency" },
        ...references,
        { path: "originalInvoiceReference", kind: "identifier", fieldPath: "originalInvoiceReference" },
      ],
    },
    { key: "supplier", fields: partySpecs("supplier") },
    { key: "customer", fields: partySpecs("customer") },
    {
      key: "totals",
      fields: [
        { path: "subtotalNet", kind: "money", fieldPath: "subtotalNet" },
        { path: "totalTax", kind: "money", fieldPath: "totalTax" },
        { path: "totalGross", kind: "money", fieldPath: "totalGross" },
        { path: "amountDue", kind: "money", fieldPath: "amountDue" },
        { path: "rounding", kind: "money", fieldPath: "rounding" },
      ],
    },
    {
      key: "payment",
      fields: [
        { path: "paymentTerms.dueDate", kind: "date", fieldPath: "paymentTerms" },
        { path: "paymentTerms.discountPercent", kind: "number", fieldPath: "paymentTerms" },
        { path: "paymentTerms.discountUntil", kind: "date", fieldPath: "paymentTerms" },
        { path: "paymentTerms.paymentMeans", kind: "enum", fieldPath: "paymentTerms", options: paymentMeans },
        { path: "paymentTerms.paymentReference", kind: "string", fieldPath: "paymentTerms" },
        { path: "paymentTerms.iban", kind: "string", fieldPath: "paymentTerms" },
        { path: "paymentTerms.bic", kind: "string", fieldPath: "paymentTerms" },
        { path: "paymentTerms.rawText", kind: "text", fieldPath: "paymentTerms" },
      ],
    },
    { key: "notes", fields: [{ path: "notes", kind: "text", fieldPath: "notes" }] },
  ];
}

/** The editable columns of the line-item grid, in order. */
export const lineColumns: ReadonlyArray<{ name: string; kind: FieldKind; options?: readonly string[] }> = [
  { name: "supplierSku", kind: "string" },
  { name: "description", kind: "string" },
  { name: "quantity", kind: "number" },
  { name: "unit", kind: "enum", options: unitsOfMeasure },
  { name: "unitPrice", kind: "money" },
  { name: "discountPercent", kind: "number" },
  { name: "discountAmount", kind: "money" },
  { name: "lineNet", kind: "money" },
  { name: "taxRate", kind: "number" },
  { name: "lineGross", kind: "money" },
];

export function lineSpecs(canonical: CanonicalInvoice | null | undefined): FieldSpec[] {
  const specs: FieldSpec[] = [];
  (canonical?.lines ?? []).forEach((_, i) => {
    for (const column of lineColumns) {
      const path = `lines[${i}].${column.name}`;
      specs.push({ path, kind: column.kind, fieldPath: path, ...(column.options ? { options: column.options } : {}) });
    }
  });
  return specs;
}

export function allSpecs(canonical: CanonicalInvoice | null | undefined): FieldSpec[] {
  return [...headerSections(canonical).flatMap((section) => section.fields), ...lineSpecs(canonical)];
}

/** The `FieldValue` a spec belongs to, read from the canonical JSON. */
export function fieldOf(canonical: unknown, spec: FieldSpec): FieldValue | undefined {
  const value = getByPath(canonical, spec.fieldPath);
  return isFieldValue(value) ? value : undefined;
}

/** The raw value a spec addresses: the field's `value`, or a property inside it for nested specs. */
export function rawValueOf(canonical: unknown, spec: FieldSpec): unknown {
  const field = fieldOf(canonical, spec);
  if (!field) return undefined;
  if (spec.path === spec.fieldPath) return field.value;
  const rest = parsePath(spec.path).slice(parsePath(spec.fieldPath).length);
  return getByPath(field.value, rest);
}

/** The text the form shows for a raw canonical value. */
export function toEditable(kind: FieldKind, raw: unknown): string {
  if (raw === null || raw === undefined) return "";
  switch (kind) {
    case "identifier":
      return typeof raw === "object" ? String((raw as DocumentIdentifier).verbatim ?? "") : String(raw);
    case "money":
      return typeof raw === "object" ? numberToText((raw as Money).amount) : numberToText(raw);
    case "number":
      return numberToText(raw);
    case "date":
      return String(raw).slice(0, 10);
    default:
      return String(raw);
  }
}

function numberToText(value: unknown): string {
  if (typeof value === "number") return Number.isFinite(value) ? String(value) : "";
  if (typeof value === "string") return value;
  return "";
}

/** react-hook-form addresses nested values with dots only: `lines[2].quantity` → `lines.2.quantity`. */
export function formName(path: string): string {
  return parsePath(path)
    .map((segment) => String(segment))
    .join(".");
}

/** The inverse of `formName`. */
export function pathOfFormName(name: string): string {
  return joinPath(name.split(".").map((segment) => (/^\d+$/.test(segment) ? Number(segment) : segment)));
}

/** DOM id of a field's input, so flags and overlay boxes can focus it. */
export function fieldDomId(path: string): string {
  return `field-${path.replace(/[^\w-]/g, "_")}`;
}

/** The form's initial values: a nested object with the editable text of every spec. */
export function toFormValues(canonical: CanonicalInvoice | null | undefined, specs: FieldSpec[]): Record<string, unknown> {
  let values: Record<string, unknown> = {};
  for (const spec of specs) {
    values = setByPath(values, spec.path, toEditable(spec.kind, rawValueOf(canonical, spec)));
  }
  return values;
}

/** The currency a money field carries, else the document's. */
export function currencyOf(canonical: CanonicalInvoice | null | undefined, spec: FieldSpec): string | null {
  const raw = rawValueOf(canonical, spec);
  if (raw && typeof raw === "object" && typeof (raw as Money).currency === "string") return (raw as Money).currency;
  const currency = canonical?.currency?.value;
  return typeof currency === "string" ? currency : null;
}
