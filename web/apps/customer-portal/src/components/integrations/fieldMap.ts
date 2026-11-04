/**
 * The visual field map of the Integrations UI (docs/11 §13.1, §19): rows of "target path ← source field, formatter,
 * lookup" that compile to the JSONata expression the server renders. The API stores only the expression (engine
 * `jsonata`; a `fieldmap` engine is not built), so the map travels with it as a leading block comment
 * (`/* docproc:fieldmap {...} *\/`), which JSONata ignores. Opening a version reads the map back and compiles it again:
 * when that gives the stored expression the visual map is in sync; when the expression was edited in code mode it is
 * not, and the editor stays in code mode until the user replaces the code with the map.
 */

/** A value written as a JSON literal: text is quoted, a number written as typed, `true`/`false`, or `null`. */
export interface Literal {
  type: "text" | "number" | "boolean" | "null";
  value: string;
}

export type Formatter =
  /** `$fmtDate(value, pattern)`: a .NET custom pattern such as `dd.MM.yyyy` or `yyyyMMdd`. */
  | { kind: "date"; pattern: string }
  /** `$fmtNum(value, decimals, decimalSeparator?, groupSeparator?)`. */
  | { kind: "number"; decimals: number; decimalSeparator?: string; groupSeparator?: string }
  /** `$substring(value, 0, length)`: the first characters of a text. */
  | { kind: "truncate"; length: number };

/** A field of the values view, optionally with a second field when it is empty, a lookup, a formatter, a constant when empty and a required check. */
export interface ValueRow {
  kind: "value";
  target: string;
  /** A values-view path (`roles.validFrom`); inside a list, relative to the row (`price.amount`), or from the top as `$root.roles.validFrom`. */
  source: string;
  fallback?: string;
  lookup?: { table: string; default?: string };
  format?: Formatter;
  whenEmpty?: Literal;
  /** `$required(value, message)`: the render fails with this message when the value is missing. */
  required?: string;
}

export interface ConstantRow {
  kind: "constant";
  target: string;
  value: Literal;
}

/** A JSONata expression of its own, for what the other rows cannot say (`doc.priceBasis = "Gross" ? "B" : "N"`). */
export interface ExpressionRow {
  kind: "expression";
  target: string;
  expression: string;
}

/**
 * One output row per row of a list of the values view (`doc.lines`), with an optional JSONata filter
 * (`$not(status = "Discontinued")`). A list whose only row has an empty target is a list of plain values.
 */
export interface ListRow {
  kind: "list";
  target: string;
  source: string;
  filter?: string;
  rows: MapRow[];
}

export type MapRow = ValueRow | ConstantRow | ExpressionRow | ListRow;

export interface FieldMap {
  version: 1;
  rows: MapRow[];
}

export const FIELD_MAP_MARKER = "docproc:fieldmap";

export type FieldMapProblemCode =
  | "targetMissing"
  | "targetDuplicate"
  | "targetConflict"
  | "sourceMissing"
  | "sourceInvalid"
  | "expressionMissing"
  | "numberInvalid"
  | "lookupMissing"
  | "listEmpty"
  | "formatInvalid";

/** A problem of one row: `at` is the row's place (`3`, `6.2` for the third row inside the seventh). */
export interface FieldMapProblem {
  at: string;
  code: FieldMapProblemCode;
}

const IDENTIFIER = /^[A-Za-z_][A-Za-z0-9_]*$/;
const NUMBER = /^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$/;

/** A JSON string literal, which is also a JSONata string literal. */
const str = (text: string) => JSON.stringify(text);

export function literal(value: Literal): string {
  switch (value.type) {
    case "number":
      return NUMBER.test(value.value.trim()) ? value.value.trim() : "null";
    case "boolean":
      return value.value === "true" ? "true" : "false";
    case "null":
      return "null";
    default:
      return str(value.value);
  }
}

/** Whether a source path can be compiled: dot-separated names, optionally starting at `$root` or `$`. */
export function isValidPath(path: string): boolean {
  const segments = path.trim().split(".");
  if (segments.length === 0 || path.trim() === "") return false;
  return segments.every((segment, i) => (i === 0 && (segment === "$root" || segment === "$")) || (segment.length > 0 && !segment.includes("`") && segment.trim() === segment));
}

/** A path as JSONata: plain names as they are, any other name in backticks (`` doc.ext.`store-no` ``). */
export function compilePath(path: string): string {
  return path
    .trim()
    .split(".")
    .map((segment, i) => ((i === 0 && (segment === "$root" || segment === "$")) || IDENTIFIER.test(segment) ? segment : `\`${segment}\``))
    .join(".");
}

function formatted(value: string, format: Formatter): string {
  switch (format.kind) {
    case "date":
      return `$fmtDate(${value}, ${str(format.pattern)})`;
    case "number": {
      const separators = format.decimalSeparator !== undefined || format.groupSeparator !== undefined ? `, ${str(format.decimalSeparator ?? ".")}${format.groupSeparator !== undefined ? `, ${str(format.groupSeparator)}` : ""}` : "";
      return `$fmtNum(${value}, ${Math.trunc(format.decimals)}${separators})`;
    }
    case "truncate":
      return `$substring(${value}, 0, ${Math.trunc(format.length)})`;
  }
}

/** The JSONata of one value row: source (or its fallback), then the lookup, the formatter, the constant when empty and the required check. */
export function compileValue(row: ValueRow): string {
  const source = compilePath(row.source);
  const base = row.fallback?.trim() ? `(${source} ? ${source} : ${compilePath(row.fallback)})` : source;
  let value = base;
  if (row.lookup?.table.trim()) value = `$table(${str(row.lookup.table.trim())}, ${value}${row.lookup.default !== undefined && row.lookup.default !== "" ? `, ${str(row.lookup.default)}` : ""})`;
  if (row.format) value = formatted(value, row.format);
  if (row.whenEmpty) value = `(${base} ? ${value} : ${literal(row.whenEmpty)})`;
  if (row.required !== undefined && row.required.trim() !== "") value = `$required(${value}, ${str(row.required.trim())})`;
  return value;
}

/** An output object under construction: keys in the order the rows first name them. */
type ObjectNode = { kind: "object"; entries: Map<string, Node> };
type Node = ObjectNode | { kind: "leaf"; row: MapRow };

function tree(rows: MapRow[]): ObjectNode {
  const root: ObjectNode = { kind: "object", entries: new Map() };
  for (const row of rows) {
    const segments = row.target.split(".");
    let node: ObjectNode = root;
    for (const [i, segment] of segments.entries()) {
      if (i === segments.length - 1) {
        if (!node.entries.has(segment)) node.entries.set(segment, { kind: "leaf", row });
        break;
      }
      const next: Node | undefined = node.entries.get(segment);
      if (next && next.kind !== "object") break; // a conflict, which fieldMapProblems names
      const child: ObjectNode = next ?? { kind: "object", entries: new Map() };
      if (!next) node.entries.set(segment, child);
      node = child;
    }
  }
  return root;
}

function rowExpression(row: MapRow, indent: string): string {
  switch (row.kind) {
    case "value":
      return compileValue(row);
    case "constant":
      return literal(row.value);
    case "expression":
      return row.expression.trim();
    case "list": {
      const source = compilePath(row.source);
      const filtered = row.filter?.trim() ? `${source}[${row.filter.trim()}]` : source;
      const only = row.rows.length === 1 ? row.rows[0]! : null;
      if (only && only.target.trim() === "") {
        // A list of plain values: `[ doc.lines[status = "Discontinued"].supplierSku ]`.
        const item = rowExpression(only, indent);
        return only.kind === "value" && item === compilePath(only.source) ? `[ ${filtered}.${item} ]` : `[ ${filtered}.(${item}) ]`;
      }
      return `[ ${filtered}.${objectExpression(tree(row.rows), indent)} ]`;
    }
  }
}

function objectExpression(node: Node, indent: string): string {
  if (node.kind !== "object") return rowExpression(node.row, indent);
  if (node.entries.size === 0) return "{}";
  const inner = `${indent}  `;
  const lines = [...node.entries].map(([key, child]) => `${inner}${str(key)}: ${child.kind === "object" ? objectExpression(child, inner) : rowExpression(child.row, inner)}`);
  return `{\n${lines.join(",\n")}\n${indent}}`;
}

/** The map as it is stored at the head of the expression (`*\/` inside the JSON is written `*\/`, which JSON reads back as is). */
export function mapComment(map: FieldMap): string {
  return `/* ${FIELD_MAP_MARKER} ${JSON.stringify({ version: 1, rows: map.rows }).replaceAll("*/", "*\\/")} */`;
}

/**
 * The expression of a map: the map as a comment, then `$root` bound to the values view (so a list row can read the
 * document's fields as `$root.roles.validFrom`, docs/11 §13.10) and the output object.
 */
export function compileFieldMap(map: FieldMap): string {
  return `${mapComment(map)}\n(\n  $root := $;\n  ${objectExpression(tree(map.rows), "  ")}\n)`;
}

/** The map stored at the head of an expression, or null when it has none (a template, an expression written in code mode). */
export function readFieldMap(expression: string): FieldMap | null {
  const match = new RegExp(`^/\\* ${FIELD_MAP_MARKER} (\\{[\\s\\S]*?\\}) \\*/\\n`).exec(expression);
  if (!match) return null;
  try {
    const parsed = JSON.parse(match[1]!) as { version?: unknown; rows?: unknown };
    if (parsed.version !== 1 || !Array.isArray(parsed.rows)) return null;
    return { version: 1, rows: parsed.rows as MapRow[] };
  } catch {
    return null;
  }
}

/** The map of an expression and whether the expression is still exactly what the map compiles to. */
export function fieldMapOf(expression: string): { map: FieldMap | null; inSync: boolean } {
  const map = readFieldMap(expression);
  return { map, inSync: map !== null && compileFieldMap(map) === expression };
}

/** What keeps the map from compiling to a sensible expression, row by row. */
/**
 * The separators of `$fmtNum` as the renderer accepts them (a function error otherwise): with decimals the decimal
 * separator is not empty and differs from the group separator, and no separator holds a digit or a sign, so the text
 * reads back as the same number.
 */
export function separatorsValid(format: { decimals: number; decimalSeparator?: string; groupSeparator?: string }): boolean {
  const decimal = format.decimalSeparator ?? ".";
  const group = format.groupSeparator ?? "";
  if (/[\p{Nd}+\-−]/u.test(decimal + group)) return false;
  return Math.trunc(format.decimals) === 0 || (decimal !== "" && decimal !== group);
}

export function fieldMapProblems(map: FieldMap): FieldMapProblem[] {
  const problems: FieldMapProblem[] = [];
  const check = (rows: MapRow[], prefix: string, inList: boolean) => {
    const scalarList = inList && rows.length === 1 && rows[0]!.target.trim() === "";
    const seen = new Set<string>();
    rows.forEach((row, i) => {
      const at = `${prefix}${i}`;
      const target = row.target.trim();
      if (!scalarList) {
        if (target === "" || target.split(".").some((segment) => segment === "")) problems.push({ at, code: "targetMissing" });
        else if (seen.has(target)) problems.push({ at, code: "targetDuplicate" });
        else if ([...seen].some((other) => other.startsWith(`${target}.`) || target.startsWith(`${other}.`))) problems.push({ at, code: "targetConflict" });
        seen.add(target);
      }
      switch (row.kind) {
        case "value":
          if (row.source.trim() === "") problems.push({ at, code: "sourceMissing" });
          else if (!isValidPath(row.source) || (row.fallback?.trim() && !isValidPath(row.fallback))) problems.push({ at, code: "sourceInvalid" });
          if (row.lookup && row.lookup.table.trim() === "") problems.push({ at, code: "lookupMissing" });
          if (row.whenEmpty?.type === "number" && !NUMBER.test(row.whenEmpty.value.trim())) problems.push({ at, code: "numberInvalid" });
          if (row.format?.kind === "date" && row.format.pattern.trim() === "") problems.push({ at, code: "formatInvalid" });
          if ((row.format?.kind === "number" && !(row.format.decimals >= 0 && row.format.decimals <= 10 && separatorsValid(row.format))) || (row.format?.kind === "truncate" && !(row.format.length >= 1))) problems.push({ at, code: "formatInvalid" });
          break;
        case "constant":
          if (row.value.type === "number" && !NUMBER.test(row.value.value.trim())) problems.push({ at, code: "numberInvalid" });
          break;
        case "expression":
          if (row.expression.trim() === "") problems.push({ at, code: "expressionMissing" });
          break;
        case "list":
          if (row.source.trim() === "") problems.push({ at, code: "sourceMissing" });
          else if (!isValidPath(row.source)) problems.push({ at, code: "sourceInvalid" });
          if (row.rows.length === 0) problems.push({ at, code: "listEmpty" });
          check(row.rows, `${at}.`, true);
          break;
      }
    });
  };
  check(map.rows, "", false);
  return problems;
}

/** A row at a place (`6.2`), for the editor. */
export function rowAt(rows: MapRow[], at: string): MapRow | undefined {
  const [head, ...rest] = at.split(".").map(Number);
  const row = rows[head!];
  if (!row || rest.length === 0) return row;
  return row.kind === "list" ? rowAt(row.rows, rest.join(".")) : undefined;
}

/** The rows with the row at `at` replaced by `update(row)`, or removed when it gives null; nothing else changes. */
export function updateRowAt(rows: MapRow[], at: string, update: (row: MapRow) => MapRow | null): MapRow[] {
  const [head, ...rest] = at.split(".").map(Number);
  return rows.flatMap((row, i): MapRow[] => {
    if (i !== head) return [row];
    if (rest.length === 0) {
      const next = update(row);
      return next ? [next] : [];
    }
    return row.kind === "list" ? [{ ...row, rows: updateRowAt(row.rows, rest.join("."), update) }] : [row];
  });
}

/** Rows appended to the top level (`at` null) or to the list at `at`. */
export function appendRow(rows: MapRow[], at: string | null, row: MapRow): MapRow[] {
  if (at === null) return [...rows, row];
  return updateRowAt(rows, at, (list) => (list.kind === "list" ? { ...list, rows: [...list.rows, row] } : list));
}

/** The row moved one place up or down among its siblings. */
export function moveRow(rows: MapRow[], at: string, delta: -1 | 1): MapRow[] {
  const parts = at.split(".").map(Number);
  const index = parts.pop()!;
  const move = (siblings: MapRow[]) => {
    const to = index + delta;
    if (to < 0 || to >= siblings.length) return siblings;
    const next = [...siblings];
    [next[index], next[to]] = [next[to]!, next[index]!];
    return next;
  };
  if (parts.length === 0) return move(rows);
  return updateRowAt(rows, parts.join("."), (list) => (list.kind === "list" ? { ...list, rows: move(list.rows) } : list));
}

/**
 * The lists a row lies in, outermost first, as paths of the values view (`doc.lines`, then `doc.lines[].tiers` for a
 * row of the scales inside the prices): what a field picked in the source tree is made relative to.
 */
export function listChainOf(rows: MapRow[], at: string): string[] {
  const indexes = at.split(".").map(Number);
  const chain: string[] = [];
  let siblings = rows;
  for (const index of indexes.slice(0, -1)) {
    const row = siblings[index];
    if (!row || row.kind !== "list") break;
    const source = row.source.trim();
    const absolute = source.startsWith("$root.") || chain.length === 0 ? source.replace(/^\$root\./, "") : `${chain[chain.length - 1]}[].${source}`;
    chain.push(absolute);
    siblings = row.rows;
  }
  return chain;
}

/**
 * A path picked in the source tree (`doc.lines[].tiers[].minQuantity`) as the source of a row in the lists of `chain`:
 * relative to the innermost list it lies in (`minQuantity`), else from the top, as `$root.roles.validFrom` inside a list.
 */
export function relativeSource(path: string, chain: string[]): string {
  for (let i = chain.length - 1; i >= 0; i--) {
    const prefix = `${chain[i]}[].`;
    if (path.startsWith(prefix)) return path.slice(prefix.length).replace(/\[\]\./g, ".");
  }
  const flat = path.replace(/\[\]\./g, ".");
  return chain.length > 0 ? `$root.${flat}` : flat;
}

/** The first row of a list of the view (`doc.lines`), merged with the keys of the next few rows, for the paths inside a list. */
export function listSample(view: unknown, path: string): unknown {
  let node: unknown = view;
  for (const segment of path.replace(/^\$root\./, "").split(".")) {
    if (node === null || typeof node !== "object" || Array.isArray(node)) return undefined;
    node = (node as Record<string, unknown>)[segment];
  }
  if (!Array.isArray(node)) return undefined;
  const merged: Record<string, unknown> = {};
  for (const item of node.slice(0, 25)) {
    if (item && typeof item === "object" && !Array.isArray(item)) {
      for (const [key, value] of Object.entries(item as Record<string, unknown>)) {
        if (merged[key] === undefined || merged[key] === null || (Array.isArray(merged[key]) && (merged[key] as unknown[]).length === 0)) merged[key] = value;
      }
    }
  }
  return merged;
}

/** The lists of a rendered output and their sizes: `prices` 412, `prices[].scales` 77 in 41 rows (docs/11 §13.10's preview). */
export interface OutputList {
  path: string;
  items: number;
  /** For a list inside a list: in how many of the outer rows it is not empty. */
  inRows?: number;
}

export function outputLists(output: unknown): OutputList[] {
  const lists: OutputList[] = [];
  const visit = (value: unknown, path: string, depth: number) => {
    if (depth > 2 || value === null || typeof value !== "object") return;
    if (Array.isArray(value)) return;
    for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
      const childPath = path ? `${path}.${key}` : key;
      if (Array.isArray(child)) {
        lists.push({ path: childPath, items: child.length });
        const nested = new Map<string, { items: number; inRows: number }>();
        for (const item of child) {
          if (item && typeof item === "object" && !Array.isArray(item)) {
            for (const [innerKey, inner] of Object.entries(item as Record<string, unknown>)) {
              if (!Array.isArray(inner)) continue;
              const entry = nested.get(innerKey) ?? { items: 0, inRows: 0 };
              entry.items += inner.length;
              if (inner.length > 0) entry.inRows += 1;
              nested.set(innerKey, entry);
            }
          }
        }
        for (const [innerKey, entry] of nested) lists.push({ path: `${childPath}[].${innerKey}`, items: entry.items, inRows: entry.inRows });
      } else visit(child, childPath, depth + 1);
    }
  };
  visit(output, "", 0);
  return lists;
}

/**
 * docs/11 §13.10's `article-price-import` v1 as a visual map: what the demo builds in the Integrations UI. The compiled
 * expression renders what the hand-written one of §13.10 renders (`ArticlePriceFieldMapTests` on the server side).
 */
export const articlePriceImportMap: FieldMap = {
  version: 1,
  rows: [
    { kind: "value", target: "header.vendorNo", source: "supplier.attributes.erpVendorId", required: "Supplier has no ERP vendor id" },
    { kind: "expression", target: "header.priceListId", expression: 'roles.documentNumber ? roles.documentNumber : supplier.attributes.erpVendorId & "-" & roles.validFrom' },
    { kind: "value", target: "header.validFrom", source: "roles.validFrom", format: { kind: "date", pattern: "yyyyMMdd" } },
    { kind: "value", target: "header.validTo", source: "roles.validUntil", format: { kind: "date", pattern: "yyyyMMdd" }, whenEmpty: { type: "text", value: "99991231" } },
    { kind: "value", target: "header.currency", source: "roles.currency" },
    { kind: "expression", target: "header.priceType", expression: 'doc.priceBasis = "Gross" ? "B" : "N"' },
    { kind: "expression", target: "header.mode", expression: 'doc.listScope = "Changes" ? "UPSERT" : "REPLACE"' },
    { kind: "value", target: "header.dmsId", source: "meta.documentId" },
    { kind: "value", target: "header.revision", source: "meta.version" },
    {
      kind: "list",
      target: "prices",
      source: "doc.lines",
      filter: '$not(status = "Discontinued")',
      rows: [
        { kind: "value", target: "vendorArticleNo", source: "supplierSku" },
        { kind: "value", target: "ean", source: "gtin" },
        { kind: "value", target: "text", source: "description", format: { kind: "truncate", length: 40 } },
        { kind: "value", target: "group", source: "category" },
        { kind: "value", target: "orderUnit", source: "unit", lookup: { table: "erp-uom", default: "ST" } },
        { kind: "value", target: "contentQty", source: "packSize" },
        { kind: "value", target: "contentUnit", source: "packUnit", lookup: { table: "erp-uom", default: "ST" } },
        { kind: "value", target: "priceUnit", source: "priceQuantity", whenEmpty: { type: "number", value: "1" } },
        { kind: "value", target: "price", source: "price.amount" },
        { kind: "value", target: "validFrom", source: "lineValidFrom", fallback: "$root.roles.validFrom", format: { kind: "date", pattern: "yyyyMMdd" } },
        {
          kind: "list",
          target: "scales",
          source: "tiers",
          rows: [
            { kind: "value", target: "fromQty", source: "minQuantity" },
            { kind: "value", target: "price", source: "price.amount" },
          ],
        },
      ],
    },
    { kind: "list", target: "delist", source: "doc.lines", filter: 'status = "Discontinued"', rows: [{ kind: "value", target: "", source: "supplierSku" }] },
  ],
};
