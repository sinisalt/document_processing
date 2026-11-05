/**
 * The editable shape of a type definition (docs/11 §5.2) as the type editor changes it: plain JSON, so what the editor
 * saves is exactly what `PUT /document-types/{key}/versions/{v}` stores. Only the parts the P1 editor edits are typed;
 * every other attribute (rules, processing, classification, extraction hints) is kept as it was read.
 */
export type LabelMap = Record<string, string>;

export interface DefinitionOption {
  code: string;
  label?: LabelMap | null;
  [other: string]: unknown;
}

export interface DefinitionField {
  id: string;
  key: string;
  kind: string;
  label?: LabelMap | null;
  description?: string | null;
  required?: boolean;
  indexed?: boolean;
  section?: string | null;
  options?: DefinitionOption[] | null;
  currencyFrom?: string | null;
  codeList?: string | null;
  list?: boolean;
  readOnly?: boolean;
  provenance?: boolean;
  item?: { fields: DefinitionField[] } | null;
  fields?: DefinitionField[] | null;
  [other: string]: unknown;
}

export interface Definition {
  key: string;
  version: number;
  name: LabelMap;
  ui?: { sections?: Array<{ id: string; label?: LabelMap | null }> } | null;
  fields: DefinitionField[];
  [other: string]: unknown;
}

/** Where a field is: its index at each level, through repeating groups (`item.fields`) and groups (`fields`). */
export type FieldLocation = readonly number[];

/** The kinds the editor offers (docs/11 §5.2), most common first. */
export const editorKinds = [
  "string",
  "text",
  "identifier",
  "date",
  "decimal",
  "integer",
  "percent",
  "money",
  "currency",
  "boolean",
  "enum",
  "unit",
  "period",
  "party",
  "address",
  "paymentTerms",
  "array",
  "object",
  "bag",
] as const;

/** The editor's languages for labels: the UI languages first; a definition's other locales are kept and shown too. */
export const editorLocales = ["en", "de"] as const;

export const FIELD_KEY = /^[a-z][A-Za-z0-9_]{0,63}$/;
export const TYPE_KEY = /^[A-Za-z][A-Za-z0-9_.-]{0,63}$/;

/** The fields inside a field: a repeating group's row fields, or a group's members; null for any other kind. */
export function childrenOf(field: DefinitionField): DefinitionField[] | null {
  if (field.kind === "array") return field.item?.fields ?? [];
  if (field.kind === "object") return field.fields ?? [];
  return null;
}

function withChildren(field: DefinitionField, children: DefinitionField[]): DefinitionField {
  return field.kind === "array" ? { ...field, item: { ...(field.item ?? {}), fields: children } } : { ...field, fields: children };
}

export function fieldAt(definition: Definition, location: FieldLocation): DefinitionField | null {
  let list: DefinitionField[] | null = definition.fields;
  let field: DefinitionField | null = null;
  for (const index of location) {
    field = list?.[index] ?? null;
    if (!field) return null;
    list = childrenOf(field);
  }
  return field;
}

/**
 * Whether the field at `location` lies under a repeating group (a row field such as `lines[*].supplierSku`): it has
 * one value per row, and the field index holds only the first rows' values (docs/11 §5.8 advises against indexing it).
 */
export function isRowField(definition: Definition, location: FieldLocation): boolean {
  for (let depth = 1; depth < location.length; depth++) {
    if (fieldAt(definition, location.slice(0, depth))?.kind === "array") return true;
  }
  return false;
}

/** The sibling list a location is in, changed by `change`, rebuilt up to the root. */
function changeList(definition: Definition, parent: FieldLocation, change: (list: DefinitionField[]) => DefinitionField[]): Definition {
  const rebuild = (list: DefinitionField[], depth: number): DefinitionField[] => {
    if (depth === parent.length) return change([...list]);
    const index = parent[depth]!;
    return list.map((field, i) => (i === index ? withChildren(field, rebuild(childrenOf(field) ?? [], depth + 1)) : field));
  };
  return { ...definition, fields: rebuild(definition.fields, 0) };
}

export function updateField(definition: Definition, location: FieldLocation, patch: Partial<DefinitionField>): Definition {
  const index = location[location.length - 1]!;
  return changeList(definition, location.slice(0, -1), (list) => list.map((field, i) => (i === index ? normalizeKind({ ...field, ...patch }, field.kind) : field)));
}

export function removeField(definition: Definition, location: FieldLocation): Definition {
  const index = location[location.length - 1]!;
  return changeList(definition, location.slice(0, -1), (list) => list.filter((_, i) => i !== index));
}

/** Moves a field up (-1) or down (+1) among its siblings; returns its new location. */
export function moveField(definition: Definition, location: FieldLocation, delta: -1 | 1): { definition: Definition; location: FieldLocation } {
  const index = location[location.length - 1]!;
  const parent = location.slice(0, -1);
  const siblings = parent.length === 0 ? definition.fields : (childrenOf(fieldAt(definition, parent)!) ?? []);
  const target = index + delta;
  if (target < 0 || target >= siblings.length) return { definition, location };
  const next = changeList(definition, parent, (list) => {
    const [moved] = list.splice(index, 1);
    list.splice(target, 0, moved!);
    return list;
  });
  return { definition: next, location: [...parent, target] };
}

/** Adds a field at the end of the top level (`parent` null) or of a repeating group's or group's fields; returns its location. */
export function addField(definition: Definition, parent: FieldLocation | null, field: DefinitionField): { definition: Definition; location: FieldLocation } {
  const at = parent ?? [];
  const siblings = at.length === 0 ? definition.fields : (childrenOf(fieldAt(definition, at)!) ?? []);
  return { definition: changeList(definition, at, (list) => [...list, field]), location: [...at, siblings.length] };
}

function allFields(fields: readonly DefinitionField[]): DefinitionField[] {
  return fields.flatMap((field) => [field, ...allFields(childrenOf(field) ?? [])]);
}

/** Eight random characters of `[a-z0-9]`. */
export function randomIdSuffix(): string {
  const bytes = new Uint8Array(8);
  crypto.getRandomValues(bytes);
  return Array.from(bytes, (b) => "abcdefghijklmnopqrstuvwxyz0123456789"[b % 36]).join("");
}

/**
 * A new field id (`fld_c_k3j9x2q1`): ids are stable and never reused (docs/11 §5.2). A counter over the draft's own
 * fields would hand a removed field's id to the next new field, and the publish checks, which match fields by id,
 * would then take a removed field and a new one for a rename. A random suffix is new to every version of the type
 * without knowing them; `taken` (and the draft's own ids) are avoided all the same.
 */
export function newFieldId(definition: Definition, taken: Iterable<string> = [], random: () => string = randomIdSuffix): string {
  const used = new Set([...allFields(definition.fields).map((f) => f.id), ...taken]);
  for (;;) {
    const id = `fld_c_${random()}`;
    if (!used.has(id)) return id;
  }
}

/** A key no sibling has, from a base (`newField`, `newField2`, …). */
export function newFieldKey(siblings: readonly DefinitionField[], base = "newField"): string {
  const used = new Set(siblings.map((f) => f.key));
  if (!used.has(base)) return base;
  for (let n = 2; ; n++) if (!used.has(`${base}${n}`)) return `${base}${n}`;
}

/**
 * Keeps a field's attributes consistent with its kind after a change: a repeating group has row fields, a group has
 * members, an enum has options, money takes its currency from the document's currency field; attributes of the old
 * kind that the new one does not take are dropped (the meta-schema refuses them, `additionalProperties: false`).
 */
export function normalizeKind(field: DefinitionField, previousKind: string): DefinitionField {
  if (field.kind === previousKind) return field;
  const next: DefinitionField = { ...field };
  if (next.kind !== "array") delete next.item;
  if (next.kind !== "object") delete next.fields;
  if (next.kind !== "enum") delete next.options;
  if (next.kind !== "money") delete next.currencyFrom;
  if (next.kind !== "unit" && next.kind !== "enum") delete next.codeList;
  if (next.kind === "array") next.item = next.item ?? { fields: [] };
  if (next.kind === "object") next.fields = next.fields ?? [];
  if (next.kind === "enum") next.options = next.options ?? [];
  if (next.kind === "unit") next.codeList = "uom";
  if (next.kind === "money") next.currencyFrom = next.currencyFrom ?? "currency";
  if (next.kind === "array" || next.kind === "object" || next.kind === "bag") {
    delete next.indexed;
    delete next.list;
  }
  return next;
}

export interface FieldIssue {
  location: FieldLocation;
  code: "keyInvalid" | "keyDuplicate" | "optionsMissing" | "optionCodeDuplicate" | "indexedKind";
}

/**
 * What the editor checks before a save, so a reviewer of the definition sees it beside the field instead of in a
 * refused request: keys of the field grammar, unique among siblings, enums with options, unique codes, and `indexed`
 * only on a single value (the API's meta-schema and publish checks remain the authority).
 */
export function fieldIssues(definition: Definition): FieldIssue[] {
  const issues: FieldIssue[] = [];
  const visit = (fields: readonly DefinitionField[], parent: FieldLocation) => {
    const seen = new Map<string, number>();
    fields.forEach((field, index) => {
      const location = [...parent, index];
      if (!FIELD_KEY.test(field.key)) issues.push({ location, code: "keyInvalid" });
      else if (seen.has(field.key)) issues.push({ location, code: "keyDuplicate" });
      seen.set(field.key, index);
      if (field.kind === "enum" && !(field.options ?? []).some((o) => o.code.trim() !== "")) issues.push({ location, code: "optionsMissing" });
      const codes = (field.options ?? []).map((o) => o.code);
      if (new Set(codes).size !== codes.length) issues.push({ location, code: "optionCodeDuplicate" });
      if (field.indexed && ["array", "object", "bag", "party", "address", "paymentTerms", "period"].includes(field.kind)) issues.push({ location, code: "indexedKind" });
      const children = childrenOf(field);
      if (children) visit(children, location);
    });
  };
  visit(definition.fields, []);
  return issues;
}

/** The minimal definition of a new type of the tenant's own (docs/11 §5.4): a number to find it by, nothing else yet. */
export function newTypeDefinition(key: string, name: LabelMap): Definition {
  return {
    key,
    version: 1,
    name,
    roles: { documentNumber: "documentNumber", documentDate: "documentDate" },
    ui: { sections: [{ id: "document", label: { en: "Document", de: "Beleg" } }] },
    fields: [
      { id: "fld_number", key: "documentNumber", kind: "identifier", label: { en: "Document no.", de: "Belegnummer" }, indexed: true, section: "document" },
      { id: "fld_date", key: "documentDate", kind: "date", label: { en: "Document date", de: "Belegdatum" }, section: "document" },
    ],
    processing: { review: { autoApprove: false } },
  };
}

export function isDefinition(value: unknown): value is Definition {
  if (!value || typeof value !== "object") return false;
  const d = value as Partial<Definition>;
  return typeof d.key === "string" && typeof d.version === "number" && Array.isArray(d.fields);
}
