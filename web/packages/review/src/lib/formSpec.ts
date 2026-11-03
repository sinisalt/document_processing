import { getByPath, isFieldValue } from "@docproc/app-shell";

/**
 * The compiled form spec of a document type version (docs/11 §5.5, artifact 3), as `DocumentDetail.definition.formSpec`
 * and `GET /document-types/{key}/versions/{v}?include=formSpec` serve it (`DefinitionCompiler.CompileFormSpec`). Only the
 * attributes the review form reads are typed; anything else passes through.
 */
export type Locales = Readonly<Record<string, string>>;

export interface FormSpecOption {
  code: string;
  label?: Locales;
}

export interface FormSpecField {
  /** The definition path: `supplier`, `lines[*].quantity`, `lines[*].tiers[*].price`. */
  path: string;
  fieldId?: string;
  key: string;
  kind: string;
  label?: Locales;
  description?: string;
  section?: string;
  required?: boolean;
  readOnly?: boolean;
  list?: boolean;
  deprecated?: boolean;
  sensitive?: boolean;
  indexed?: boolean;
  /** False for a bare member the tree stores without a `FieldValue` envelope (a line's number, its printed unit). */
  provenance?: boolean;
  options?: readonly FormSpecOption[];
  codeList?: string;
  currencyPath?: string;
  visibleWhen?: { path: string; equals: unknown };
  pattern?: string;
  min?: number;
  max?: number;
  maxLength?: number;
  maxItems?: number;
  anchor?: string;
  members?: ReadonlyArray<{ key: string; kind: string }>;
  children?: readonly FormSpecField[];
  /**
   * Web only, never compiled: a row field the grid shows as a column even when no row has a value in it. The built-in
   * invoice form (`legacyFormSpec`) sets it for what the line check is about; compiled specs show required columns.
   */
  alwaysShown?: boolean;
}

export interface FormSpecSection {
  id: string;
  label?: Locales;
}

export interface FormSpec {
  formatVersion: number;
  key: string;
  version: number;
  name?: Locales;
  sections: readonly FormSpecSection[];
  fields: readonly FormSpecField[];
}

export function isFormSpec(value: unknown): value is FormSpec {
  if (value === null || typeof value !== "object") return false;
  const spec = value as Partial<FormSpec>;
  return typeof spec.key === "string" && Array.isArray(spec.fields) && Array.isArray(spec.sections ?? []);
}

/** How a value is edited and which JSON shape `PATCH /fields` expects for it (docs/05 "shorthands"). */
export type FieldKind = "string" | "identifier" | "date" | "money" | "number" | "enum" | "text" | "boolean";

export interface FieldSpec {
  /** Concrete canonical path (`invoiceNumber`, `supplier.address.city`, `lines[2].quantity`, `ext.storeNo`). */
  path: string;
  kind: FieldKind;
  /** The `FieldValue` this editable value lives in (`supplier` for `supplier.address.city`); equals `path` for whole fields. */
  fieldPath: string;
  /** Codes for `kind: "enum"`. */
  options?: readonly string[];
  /** Labels of the codes by locale, from the definition. */
  optionLabels?: Readonly<Record<string, Locales>>;
  /** The definition's label of the field (members of a composite have none: the UI language labels them). */
  label?: Locales;
  /** The definition kind (`party`, `unit`, `percent`, ...), when the spec came from a definition field. */
  definitionKind?: string;
  /** For a member of a composite value: the composite's kind and the member path inside it (`address.city`). */
  member?: { of: string; path: string };
  required?: boolean;
  /**
   * A member of a composite value the definition requires (the price list's `supplier` party): the member itself is not
   * required (the form shows no "*" beside it), but the composite is empty, and so missing, while none of its members
   * holds a value. What map mode's Enter moves on to (`nextEmptyRequired`).
   */
  inRequired?: boolean;
  readOnly?: boolean;
  /** Where the currency of a money value is when the value carries none (`currency`). */
  currencyPath?: string;
  /** Shown only when the value at `path` equals `equals`, or when the field holds a value anyway. */
  visibleWhen?: { path: string; equals: unknown };
  /** Whole numbers only (`integer`). */
  integer?: boolean;
}

/** A column of a repeating group's grid: one row field. */
export interface GridColumn {
  key: string;
  kind: FieldKind;
  field: FormSpecField;
  options?: readonly string[];
  optionLabels?: Readonly<Record<string, Locales>>;
  /** Always a column, even when no row has a value in it. */
  alwaysShown: boolean;
  /** Multi-line text: spans the first row of the line (the description), above the amounts. */
  wide: boolean;
  integer?: boolean;
}

export type FormNode =
  | { type: "field"; spec: FieldSpec }
  /** One `FieldValue` holding an object (a party, payment terms): its members are edited inside it, badged once. */
  | { type: "composite"; path: string; field: FormSpecField; specs: FieldSpec[]; inline: boolean }
  | { type: "group"; path: string; field: FormSpecField; nodes: FormNode[] }
  /** A list of values of one kind, each with its own envelope (`orderReferences[0]`). */
  | { type: "list"; path: string; field: FormSpecField; entries: FieldSpec[]; entry: Omit<FieldSpec, "path" | "fieldPath"> }
  | GridNode
  /** Supplier-specific attributes by key (`ext.storeNo`). */
  | { type: "bag"; path: string; field: FormSpecField; entries: FieldSpec[] };

export interface GridNode {
  type: "grid";
  /** Concrete path of the array (`lines`). */
  path: string;
  field: FormSpecField;
  columns: GridColumn[];
  rowCount: number;
  /** The bare member that numbers the rows (`lineNumber`), if the rows have one. */
  rowNumberKey?: string;
  /** The bare member with the unit as printed (`unitRaw`), shown under the unit. */
  printedUnitKey?: string;
  /** Repeating groups inside a row (`tiers`): shown as a count, not edited in the grid. */
  nested: FormSpecField[];
  /** Whether the line arithmetic check applies (quantity × unit price vs. line net, the invoice family's rows). */
  arithmetic: boolean;
}

export interface FormSection {
  id: string;
  label?: Locales;
  nodes: FormNode[];
}

export interface FormLayout {
  key: string;
  version: number;
  name?: Locales;
  sections: FormSection[];
  /** Every editable value of the document, in form order: what the form values and the correction diff are built from. */
  specs: FieldSpec[];
  /** The definition fields by definition path (`lines[*].quantity`), for labels. */
  fields: ReadonlyMap<string, FormSpecField>;
  /** Whether the tree is stored as a definition tree (`canonicalSchemaVersion` 2): rows and list entries can be added and removed. */
  structural: boolean;
}

export interface LayoutOptions {
  /** `DocumentDetail.canonicalSchemaVersion`: version 2 trees take `append` and `remove` and hold UN/ECE unit codes. */
  schemaVersion?: number | null;
  /**
   * Whether the document is an invoice or credit note (`Invoice`, `CreditNote`): the coded invoice record reads its tree
   * of either version, so its `unit` fields take the `UnitOfMeasure` names, never the UN/ECE codes.
   */
  invoiceFamily?: boolean;
}

/** The members of each shipped composite, mirroring `FieldKinds.CompositeMemberKinds` (`src/DocProc.Definitions/FieldKinds.cs`). */
export const compositeMembers: Readonly<Record<string, ReadonlyArray<{ key: string; kind: string }>>> = {
  identifier: [
    { key: "verbatim", kind: "string" },
    { key: "normalized", kind: "string" },
  ],
  money: [
    { key: "amount", kind: "decimal" },
    { key: "currency", kind: "currency" },
  ],
  period: [
    { key: "from", kind: "date" },
    { key: "to", kind: "date" },
  ],
  party: [
    { key: "name", kind: "string" },
    { key: "vatId", kind: "string" },
    { key: "taxId", kind: "string" },
    { key: "registrationNumber", kind: "string" },
    { key: "address", kind: "address" },
    { key: "email", kind: "string" },
    { key: "phone", kind: "string" },
    { key: "iban", kind: "string" },
    { key: "bic", kind: "string" },
  ],
  address: [
    { key: "line1", kind: "string" },
    { key: "line2", kind: "string" },
    { key: "postalCode", kind: "string" },
    { key: "city", kind: "string" },
    { key: "region", kind: "string" },
    { key: "countryCode", kind: "string" },
  ],
  taxRow: [
    { key: "rate", kind: "percent" },
    { key: "taxableAmount", kind: "money" },
    { key: "taxAmount", kind: "money" },
    { key: "category", kind: "string" },
  ],
  paymentTerms: [
    { key: "dueDate", kind: "date" },
    { key: "discountPercent", kind: "percent" },
    { key: "discountUntil", kind: "date" },
    { key: "paymentMeans", kind: "string" },
    { key: "paymentReference", kind: "string" },
    { key: "iban", kind: "string" },
    { key: "bic", kind: "string" },
    { key: "rawText", kind: "text" },
  ],
  chargeOrAllowance: [
    { key: "description", kind: "string" },
    { key: "amount", kind: "money" },
    { key: "taxRate", kind: "percent" },
    { key: "isCharge", kind: "boolean" },
  ],
  temperatureReading: [
    { key: "celsius", kind: "decimal" },
    { key: "location", kind: "string" },
    { key: "rawText", kind: "text" },
  ],
};

/** The composites whose members are edited inside one envelope (not `identifier` or `money`, which are value shapes). */
export function isComposite(kind: string): boolean {
  return kind === "period" || kind === "party" || kind === "address" || kind === "taxRow" || kind === "paymentTerms" || kind === "chargeOrAllowance" || kind === "temperatureReading";
}

export const invoiceKinds = ["Invoice", "CreditNote", "Proforma"] as const;
/** `DocProc.Domain.Invoices.PaymentMeans`: the payment terms' `paymentMeans` member takes these names. */
export const paymentMeans = ["BankTransfer", "DirectDebit", "Card", "Cash", "Other"] as const;
/**
 * `DocProc.Domain.Common.UnitOfMeasure`, in its order. A version 1 tree (the coded records) holds these names, and
 * `PATCH /fields` takes only them for `lines[i].unit` (the printed spellings such as "kg" or "Stk" are the extractor's
 * business, kept in `unitRaw`).
 */
export const unitsOfMeasure = ["Other", "Kilogram", "Gram", "Litre", "Millilitre", "Piece", "Box", "Case", "Crate", "Pallet", "Hour"] as const;
/**
 * The platform `uom` code list (`CodeLists.Platform`, `src/DocProc.Domain/Validation/RuleData.cs`): the UN/ECE
 * Recommendation 20 codes a version 2 tree holds for a `unit` field, with the unit name each stands for.
 */
export const uneceUnits: Readonly<Record<string, string>> = {
  KGM: "Kilogram",
  GRM: "Gram",
  LTR: "Litre",
  MLT: "Millilitre",
  H87: "Piece",
  BX: "Box",
  CS: "Case",
  CR: "Crate",
  PF: "Pallet",
  HUR: "Hour",
};

/** Member options the definition format does not carry (the `paymentMeans` member of payment terms is the domain enum). */
const memberOptions: Readonly<Record<string, readonly string[]>> = {
  "paymentTerms.paymentMeans": paymentMeans,
};

const SCALAR_EDITORS: Readonly<Record<string, FieldKind>> = {
  string: "string",
  currency: "string",
  text: "text",
  identifier: "identifier",
  integer: "number",
  decimal: "number",
  percent: "number",
  money: "money",
  date: "date",
  boolean: "boolean",
  enum: "enum",
  unit: "enum",
};

/** The editor of a definition kind, or null for a kind the form does not edit as one value (composites, groups, bags). */
export function editorOf(kind: string): FieldKind | null {
  return SCALAR_EDITORS[kind] ?? null;
}

function optionsOf(field: { kind: string; options?: readonly FormSpecOption[] }, units: readonly string[]): Pick<FieldSpec, "options" | "optionLabels"> {
  if (field.kind === "unit") return { options: units };
  if (field.kind !== "enum" || !field.options) return {};
  const optionLabels: Record<string, Locales> = {};
  for (const option of field.options) if (option.label) optionLabels[option.code] = option.label;
  return { options: field.options.map((option) => option.code), ...(Object.keys(optionLabels).length ? { optionLabels } : {}) };
}

/** Whether a node of the canonical tree carries a value (a Missing field serialises the type's default, which is none). */
function hasStoredValue(node: unknown): boolean {
  if (!isFieldValue(node) || node.status === "Missing") return false;
  return node.value !== null && node.value !== undefined && node.value !== "";
}

/**
 * The unit codes a `unit` field of this tree takes: the `UnitOfMeasure` names for a version 1 tree (and for every tree of
 * the invoice family, `LayoutOptions.invoiceFamily`), and for a version 2 tree whose rows already hold names; otherwise
 * the UN/ECE codes of the platform `uom` list.
 */
function unitCodes(canonical: unknown, concretePaths: readonly string[], schemaVersion: number | null | undefined): readonly string[] {
  if (schemaVersion !== 2) return unitsOfMeasure;
  const names = new Set<string>(unitsOfMeasure);
  for (const path of concretePaths) {
    const node = getByPath(canonical, path);
    if (hasStoredValue(node) && names.has(String((node as { value: unknown }).value))) return unitsOfMeasure;
  }
  return Object.keys(uneceUnits);
}

function concreteRows(canonical: unknown, path: string): unknown[] {
  const rows = getByPath(canonical, path);
  return Array.isArray(rows) ? rows : [];
}

function memberSpecs(field: FormSpecField, kind: string, base: string, fieldPath: string, memberPrefix: string, of: string, schemaVersion: number | null | undefined, canonical: unknown): FieldSpec[] {
  const members = (memberPrefix === "" ? field.members : undefined) ?? compositeMembers[kind] ?? [];
  const scalars: FieldSpec[] = [];
  const nested: FieldSpec[] = [];
  for (const member of members) {
    const memberPath = memberPrefix === "" ? member.key : `${memberPrefix}.${member.key}`;
    const path = `${base}.${member.key}`;
    if (isComposite(member.kind)) {
      nested.push(...memberSpecs(field, member.kind, path, fieldPath, memberPath, of, schemaVersion, canonical));
      continue;
    }
    const editor = editorOf(member.kind);
    if (!editor) continue;
    const options = memberOptions[`${of}.${memberPath}`];
    scalars.push({
      path,
      kind: options ? "enum" : editor,
      fieldPath,
      ...(options ? { options } : optionsOf(member, unitCodes(canonical, [], schemaVersion))),
      definitionKind: member.kind,
      member: { of, path: memberPath },
      ...(field.required ? { inRequired: true } : {}),
      ...(field.readOnly ? { readOnly: true } : {}),
      ...(field.currencyPath ? { currencyPath: field.currencyPath } : {}),
      ...(member.kind === "integer" ? { integer: true } : {}),
    });
  }
  // Nested composites (a party's address) after the plain members, as the review form always listed them.
  return [...scalars, ...nested];
}

function leafSpec(field: FormSpecField, path: string, units: readonly string[]): FieldSpec | null {
  const editor = editorOf(field.kind);
  if (!editor) return null;
  return {
    path,
    kind: editor,
    fieldPath: path,
    ...optionsOf(field, units),
    ...(field.label ? { label: field.label } : {}),
    definitionKind: field.kind,
    ...(field.required ? { required: true } : {}),
    ...(field.readOnly ? { readOnly: true } : {}),
    ...(field.currencyPath ? { currencyPath: field.currencyPath } : {}),
    ...(field.visibleWhen ? { visibleWhen: field.visibleWhen } : {}),
    ...(field.kind === "integer" ? { integer: true } : {}),
  };
}

/** The number of grid columns a repeating group always shows when its rows hold no values yet (a new delivery note). */
const MIN_COLUMNS = 6;

function gridOf(field: FormSpecField, path: string, canonical: unknown, schemaVersion: number | null | undefined, specs: FieldSpec[]): GridNode {
  const rows = concreteRows(canonical, path);
  const children = field.children ?? [];
  const columns: GridColumn[] = [];
  const nested: FormSpecField[] = [];
  let rowNumberKey: string | undefined;
  let printedUnitKey: string | undefined;
  for (const child of children) {
    if (child.provenance === false) {
      if (child.kind === "integer" && !rowNumberKey && /number/i.test(child.key)) rowNumberKey = child.key;
      else if (child.kind === "string" && /raw$/i.test(child.key) && !printedUnitKey) printedUnitKey = child.key;
      continue;
    }
    if (child.kind === "array") {
      nested.push(child);
      continue;
    }
    const editor = child.list ? null : editorOf(child.kind);
    if (!editor) continue;
    const units = child.kind === "unit" ? unitCodes(canonical, rows.map((_, i) => `${path}[${i}].${child.key}`), schemaVersion) : [];
    columns.push({
      key: child.key,
      kind: editor,
      field: child,
      ...optionsOf(child, units),
      alwaysShown: !!child.required || !!child.alwaysShown,
      wide: editor === "text",
      ...(child.kind === "integer" ? { integer: true } : {}),
    });
  }
  // A grid of empty rows still needs something to type into: its first columns are shown until values exist.
  if (!columns.some((c) => c.alwaysShown)) {
    for (const column of columns.slice(0, MIN_COLUMNS)) column.alwaysShown = true;
  }
  rows.forEach((_, index) => {
    for (const column of columns) {
      const rowPath = `${path}[${index}].${column.key}`;
      specs.push({
        path: rowPath,
        kind: column.kind,
        fieldPath: rowPath,
        ...(column.options ? { options: column.options } : {}),
        ...(column.optionLabels ? { optionLabels: column.optionLabels } : {}),
        ...(column.field.label ? { label: column.field.label } : {}),
        definitionKind: column.field.kind,
        ...(column.field.required ? { required: true } : {}),
        ...(column.field.readOnly ? { readOnly: true } : {}),
        ...(column.field.currencyPath ? { currencyPath: column.field.currencyPath } : {}),
        ...(column.integer ? { integer: true } : {}),
      });
    }
  });
  const keys = new Set(children.map((c) => c.key));
  return {
    type: "grid",
    path,
    field,
    columns,
    rowCount: rows.length,
    ...(rowNumberKey ? { rowNumberKey } : {}),
    ...(printedUnitKey ? { printedUnitKey } : {}),
    nested,
    arithmetic: keys.has("quantity") && keys.has("unitPrice") && keys.has("lineNet"),
  };
}

function nodesOf(fields: readonly FormSpecField[], prefix: string, canonical: unknown, schemaVersion: number | null | undefined, specs: FieldSpec[]): Array<{ node: FormNode; section?: string }> {
  const out: Array<{ node: FormNode; section?: string }> = [];
  for (const field of fields) {
    const path = prefix === "" ? field.key : `${prefix}.${field.key}`;
    const section = field.section;
    // Bare members (the VAT summary, charges, a purchase order's status) carry no provenance and take no correction.
    if (field.provenance === false) continue;
    const stored = getByPath(canonical, path);
    // A deprecated field is kept out of the form unless the document holds a value in it.
    if (field.deprecated && !hasStoredValue(stored) && !(Array.isArray(stored) && stored.length > 0)) continue;

    if (field.kind === "array") {
      out.push({ node: gridOf(field, path, canonical, schemaVersion, specs), section });
      continue;
    }
    if (field.kind === "object") {
      const children = nodesOf(field.children ?? [], path, canonical, schemaVersion, specs).map((entry) => entry.node);
      out.push({ node: { type: "group", path, field, nodes: children }, section });
      continue;
    }
    if (field.kind === "bag") {
      const bag = getByPath(canonical, path);
      const entries: FieldSpec[] = [];
      if (bag && typeof bag === "object" && !Array.isArray(bag)) {
        for (const key of Object.keys(bag as Record<string, unknown>)) {
          if (!/^[A-Za-z_$][\w$]*$/.test(key)) continue;
          const entryPath = `${path}.${key}`;
          const node = (bag as Record<string, unknown>)[key];
          const value = isFieldValue(node) ? node.value : undefined;
          const spec: FieldSpec = { path: entryPath, kind: typeof value === "number" ? "number" : typeof value === "boolean" ? "boolean" : "string", fieldPath: entryPath, definitionKind: "bag" };
          entries.push(spec);
          specs.push(spec);
        }
      }
      out.push({ node: { type: "bag", path, field, entries }, section });
      continue;
    }
    if (field.list) {
      const editor = editorOf(field.kind);
      if (!editor) continue;
      const rows = concreteRows(canonical, path);
      const units = field.kind === "unit" ? unitCodes(canonical, rows.map((_, i) => `${path}[${i}]`), schemaVersion) : [];
      const entry = leafSpec(field, path, units)!;
      const { path: _path, fieldPath: _fieldPath, ...template } = entry;
      const entries = rows.map((_, i) => ({ ...template, path: `${path}[${i}]`, fieldPath: `${path}[${i}]` }));
      specs.push(...entries);
      out.push({ node: { type: "list", path, field, entries, entry: template }, section });
      continue;
    }
    if (isComposite(field.kind)) {
      const members = memberSpecs(field, field.kind, path, path, "", field.kind, schemaVersion, canonical);
      specs.push(...members);
      out.push({ node: { type: "composite", path, field, specs: members, inline: field.kind === "period" }, section });
      continue;
    }
    const spec = leafSpec(field, path, field.kind === "unit" ? unitCodes(canonical, [path], schemaVersion) : []);
    if (!spec) continue;
    specs.push(spec);
    out.push({ node: { type: "field", spec }, section });
  }
  return out;
}

/** The id of the section that collects fields the definition puts in no section (or in one it does not declare). */
export const OTHER_SECTION = "__other";

/**
 * The review form of a document (docs/11 §12.2): the definition's sections in order, each with its fields as form
 * nodes (plain fields, composites with their members, groups, lists, repeating groups and bags), and every editable
 * value as a `FieldSpec` with its concrete path in the canonical tree. Rows and list entries come from `canonical`, so
 * the form has one spec per stored row. Fields without a provenance envelope are not part of the form.
 */
export function specsFromDefinition(formSpec: FormSpec, canonical: unknown, options: LayoutOptions = {}): FormLayout {
  const specs: FieldSpec[] = [];
  // The coded invoice record reads an invoice or credit note tree of either version and holds its units as
  // `UnitOfMeasure` names, so its form offers the names whatever the tree version (the server converts the rest).
  const unitVersion = options.invoiceFamily ? 1 : options.schemaVersion;
  const entries = nodesOf(formSpec.fields, "", canonical, unitVersion, specs);
  const declared = new Set((formSpec.sections ?? []).map((section) => section.id));
  const sections: FormSection[] = (formSpec.sections ?? []).map((section) => ({ id: section.id, ...(section.label ? { label: section.label } : {}), nodes: [] }));
  const other: FormSection = { id: OTHER_SECTION, nodes: [] };
  for (const { node, section } of entries) {
    const target = section && declared.has(section) ? sections.find((s) => s.id === section)! : other;
    target.nodes.push(node);
  }
  if (other.nodes.length > 0) sections.push(other);

  // The form's order is the sections' order: the correction diff and the focus lookup walk it.
  const order = new Map<string, number>();
  const visit = (nodes: FormNode[]) => {
    for (const node of nodes) {
      const paths = node.type === "field" ? [node.spec.path] : node.type === "composite" ? node.specs.map((s) => s.path) : node.type === "group" ? [] : node.type === "list" || node.type === "bag" ? node.entries.map((s) => s.path) : [];
      for (const path of paths) order.set(path, order.size);
      if (node.type === "group") visit(node.nodes);
    }
  };
  for (const section of sections) visit(section.nodes.filter((n) => n.type !== "grid"));
  specs.sort((a, b) => (order.get(a.path) ?? Number.MAX_SAFE_INTEGER) - (order.get(b.path) ?? Number.MAX_SAFE_INTEGER));

  const fields = new Map<string, FormSpecField>();
  const index = (list: readonly FormSpecField[]) => {
    for (const field of list) {
      fields.set(field.path, field);
      if (field.children) index(field.children);
    }
  };
  index(formSpec.fields);

  return {
    key: formSpec.key,
    version: formSpec.version,
    ...(formSpec.name ? { name: formSpec.name } : {}),
    sections: sections.filter((section) => section.nodes.length > 0),
    specs,
    fields,
    structural: options.schemaVersion === 2,
  };
}

/** The repeating groups of a layout, at any depth of groups. */
export function gridsOf(layout: FormLayout): GridNode[] {
  const out: GridNode[] = [];
  const visit = (nodes: FormNode[]) => {
    for (const node of nodes) {
      if (node.type === "grid") out.push(node);
      else if (node.type === "group") visit(node.nodes);
    }
  };
  for (const section of layout.sections) visit(section.nodes);
  return out;
}

/**
 * The definition path of a concrete path (`lines[2].quantity` → `lines[*].quantity`), with the row of the repeating
 * group it lies in, the entry of a list, and what is left inside a composite value (`supplier.address.city` → field
 * `supplier`, member `address.city`).
 */
export function resolvePath(fields: ReadonlyMap<string, FormSpecField>, path: string): { field: FormSpecField | null; member: string | null; row: { group: FormSpecField; index: number } | null; entry: number | null } {
  const segments = path.match(/[^.[\]]+|\[\d+\]/g) ?? [];
  let definitionPath = "";
  let field: FormSpecField | null = null;
  let row: { group: FormSpecField; index: number } | null = null;
  let entry: number | null = null;
  let i = 0;
  for (; i < segments.length; i++) {
    const segment = segments[i]!;
    if (segment.startsWith("[")) {
      const index = Number(segment.slice(1, -1));
      if (field?.kind === "array") {
        definitionPath += "[*]";
        row = { group: field, index };
      } else if (field?.list) {
        entry = index;
      }
      continue;
    }
    const candidate = definitionPath === "" ? segment : `${definitionPath}.${segment}`;
    const found = fields.get(candidate);
    if (!found) break;
    field = found;
    definitionPath = candidate;
    const next = segments[i + 1];
    if (found.list && next?.startsWith("[")) {
      entry = Number(next.slice(1, -1));
      i++;
    }
    if (found.kind === "bag" || isComposite(found.kind) || found.kind === "identifier" || found.kind === "money") {
      i++;
      break;
    }
  }
  const rest = segments.slice(i).filter((s) => !s.startsWith("["));
  return { field, member: rest.length ? rest.join(".") : null, row, entry };
}
