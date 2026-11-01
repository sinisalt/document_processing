/**
 * Canonical field paths as the API spells them (docs/05 "PATCH /documents/{id}/fields"): `invoiceNumber`,
 * `lines[2].quantity`, `supplier.vatId`. The review form reads and writes the canonical JSON through these helpers so
 * the same string that names a field in a flag or a correction addresses it in the form state.
 */
export type PathSegment = string | number;

const IDENTIFIER = /^[A-Za-z_$][\w$]*$/;

export class FieldPathError extends Error {
  constructor(
    public readonly path: string,
    reason: string,
  ) {
    super(`Invalid field path '${path}': ${reason}`);
    this.name = "FieldPathError";
  }
}

/** `'lines[2].quantity'` → `['lines', 2, 'quantity']`. Throws `FieldPathError` on anything that is not canonical. */
export function parsePath(path: string): PathSegment[] {
  if (typeof path !== "string" || path.trim() === "") throw new FieldPathError(path, "empty");
  const segments: PathSegment[] = [];
  let i = 0;
  let expectName = true;
  while (i < path.length) {
    const ch = path[i]!;
    if (ch === "[") {
      if (expectName && segments.length === 0) throw new FieldPathError(path, "must start with a property name");
      const close = path.indexOf("]", i);
      if (close < 0) throw new FieldPathError(path, "missing ']'");
      const index = path.slice(i + 1, close);
      if (!/^\d+$/.test(index)) throw new FieldPathError(path, `index '${index}' is not a non-negative integer`);
      segments.push(Number(index));
      i = close + 1;
      expectName = false;
      continue;
    }
    if (ch === ".") {
      if (expectName) throw new FieldPathError(path, "unexpected '.'");
      i++;
      expectName = true;
      continue;
    }
    if (!expectName) throw new FieldPathError(path, `unexpected '${ch}' after an index`);
    let j = i;
    while (j < path.length && path[j] !== "." && path[j] !== "[") j++;
    const name = path.slice(i, j);
    if (!IDENTIFIER.test(name)) throw new FieldPathError(path, `'${name}' is not a property name`);
    segments.push(name);
    i = j;
    expectName = false;
  }
  if (expectName) throw new FieldPathError(path, "ends with '.'");
  return segments;
}

/** `['lines', 2, 'quantity']` → `'lines[2].quantity'`. */
export function joinPath(segments: readonly PathSegment[]): string {
  let out = "";
  for (const segment of segments) {
    if (typeof segment === "number") out += `[${segment}]`;
    else out += out === "" ? segment : `.${segment}`;
  }
  return out;
}

function toSegments(path: string | readonly PathSegment[]): PathSegment[] {
  return typeof path === "string" ? parsePath(path) : [...path];
}

/** The value at `path`, or `undefined` when any step is missing. Never throws on shape, only on a malformed path. */
export function getByPath(target: unknown, path: string | readonly PathSegment[]): unknown {
  let current: unknown = target;
  for (const segment of toSegments(path)) {
    if (current === null || current === undefined) return undefined;
    if (typeof segment === "number") {
      if (!Array.isArray(current)) return undefined;
      current = current[segment];
    } else {
      if (typeof current !== "object") return undefined;
      current = (current as Record<string, unknown>)[segment];
    }
  }
  return current;
}

/**
 * Returns a copy of `target` with `value` at `path`, sharing every untouched branch (safe for React state). Missing
 * containers are created: an array for a numeric segment, an object otherwise. Setting `undefined` removes the key.
 */
export function setByPath<T>(target: T, path: string | readonly PathSegment[], value: unknown): T {
  const segments = toSegments(path);
  if (segments.length === 0) return value as T;
  return assign(target, segments, 0, value) as T;
}

function assign(current: unknown, segments: PathSegment[], index: number, value: unknown): unknown {
  const segment = segments[index]!;
  const last = index === segments.length - 1;
  if (typeof segment === "number") {
    const array = Array.isArray(current) ? [...current] : [];
    const next = last ? value : assign(array[segment], segments, index + 1, value);
    if (last && value === undefined) array.splice(segment, 1);
    else array[segment] = next;
    return array;
  }
  const object: Record<string, unknown> = current !== null && typeof current === "object" && !Array.isArray(current) ? { ...(current as Record<string, unknown>) } : {};
  if (last && value === undefined) {
    delete object[segment];
    return object;
  }
  object[segment] = last ? value : assign(object[segment], segments, index + 1, value);
  return object;
}

/** `'lines[2].quantity'` → `'lines[2]'`; a top-level path has no parent (null). */
export function parentPath(path: string): string | null {
  const segments = parsePath(path);
  return segments.length <= 1 ? null : joinPath(segments.slice(0, -1));
}

export function lastSegment(path: string): PathSegment {
  const segments = parsePath(path);
  return segments[segments.length - 1]!;
}

/** For `lines[2].quantity` → `{ index: 2, field: 'quantity' }`; null for header fields. */
export function lineItemOf(path: string): { index: number; field: string | null } | null {
  const segments = parsePath(path);
  if (segments[0] !== "lines" || typeof segments[1] !== "number") return null;
  const rest = segments.slice(2);
  return { index: segments[1], field: rest.length ? joinPath(rest) : null };
}

/** True when `path` is `prefix` or lies beneath it (`lines[2]` covers `lines[2].quantity`, not `lines[20]`). */
export function isWithin(path: string, prefix: string): boolean {
  const a = parsePath(path);
  const b = parsePath(prefix);
  return b.length <= a.length && b.every((segment, i) => segment === a[i]);
}
