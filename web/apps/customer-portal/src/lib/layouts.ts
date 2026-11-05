import type { components } from "@docproc/api-client";
import type { Box } from "@docproc/review";
import type { DocumentDetail, DocumentFlag } from "./queries";

export type SupplierLayout = components["schemas"]["SupplierLayoutResponse"];
export type SupplierLayoutVersionSummary = components["schemas"]["SupplierLayoutVersionSummary"];
export type SupplierLayoutVersion = components["schemas"]["SupplierLayoutVersionResponse"];
export type ReplayReport = components["schemas"]["ReplayReport"];
export type LayoutSuggestion = components["schemas"]["LayoutSuggestionDto"];
export type IdentificationTestResponse = components["schemas"]["IdentificationTestResponse"];
export type MatchingDocument = components["schemas"]["MatchingDocumentDto"];
export type LearnLayoutRequest = components["schemas"]["LearnLayoutRequest"];
export type NotificationItem = components["schemas"]["NotificationDto"];

/** The flags the New layout panel answers (docs/11 §12.10, §8.4): both block approval until resolved. */
export const LAYOUT_FLAG_CODES = ["NEW_LAYOUT", "LAYOUT_AMBIGUOUS"] as const;

/** The open NEW_LAYOUT or LAYOUT_AMBIGUOUS flag of a document, NEW_LAYOUT first. */
export function openLayoutFlag(document: Pick<DocumentDetail, "flags">): DocumentFlag | null {
  const open = document.flags.filter((f) => f.resolvedAt === null);
  return open.find((f) => f.code === "NEW_LAYOUT") ?? open.find((f) => f.code === "LAYOUT_AMBIGUOUS") ?? null;
}

/** An identifier of an identification rule (`schemas/identification-rule.v1.json`), as the API proposes and stores it. */
export type Identifier = Record<string, unknown>;

/** One chip of the Identification card (docs/11 §12.9 step 3): an identifier, in "must contain" or "must not contain". */
export interface IdentifierChip {
  id: string;
  identifier: Identifier;
  /** The API's sentence for it ("'Rechnung' in the page-1 title"). */
  describe: string;
  negative: boolean;
}

/** The rule being marked in Identify mode: "All of these" or "Any of these", and its chips. */
export interface IdentificationDraft {
  connective: "all" | "any";
  chips: IdentifierChip[];
}

export const EMPTY_DRAFT: IdentificationDraft = { connective: "all", chips: [] };

/** The rule the chips make, or null while no chip says what the document must contain (the API then proposes one). */
export function ruleOf(draft: IdentificationDraft): Record<string, Identifier[]> | null {
  const positive = draft.chips.filter((c) => !c.negative).map((c) => c.identifier);
  if (positive.length === 0) return null;
  const negative = draft.chips.filter((c) => c.negative).map((c) => c.identifier);
  return negative.length > 0 ? { [draft.connective]: positive, none: negative } : { [draft.connective]: positive };
}

/** Where an identifier is looked for, from narrow to wide: a box, a named region, its page, any page. */
export type IdentifierLocation = "area" | "region" | "page" | "anyPage";

export function locationOf(identifier: Identifier): IdentifierLocation {
  if (identifier.bBox !== undefined) return "area";
  if (identifier.region !== undefined) return "region";
  if (identifier.page !== undefined) return "page";
  return "anyPage";
}

/** "Widen the location" (§12.9 step 3): a box or region becomes its page, a page becomes any page. */
export function widen(identifier: Identifier): Identifier {
  const { bBox: _box, tolerance: _tolerance, region: _region, ...rest } = identifier;
  if (identifier.bBox !== undefined || identifier.region !== undefined) return rest;
  const { page: _page, ...anywhere } = rest;
  return anywhere;
}

/** A supplier value (the VAT id) or a text the reviewer can make case-sensitive. */
export function canBeCaseSensitive(identifier: Identifier): boolean {
  return typeof identifier.text === "string";
}

export function toggleCaseSensitive(identifier: Identifier): Identifier {
  if (identifier.caseSensitive === true) {
    const { caseSensitive: _c, ...rest } = identifier;
    return rest;
  }
  return { ...identifier, caseSensitive: true };
}

let chipCounter = 0;

export function newChip(identifier: Identifier, describe: string): IdentifierChip {
  chipCounter += 1;
  return { id: `chip-${chipCounter}`, identifier, describe, negative: false };
}

/** The evidence boxes of an identification result (`bBox` as [x0, y0, x1, y1] in page units) on one page. */
export function evidenceBoxes(evidence: readonly { page?: number | string | null; bBox?: readonly (number | string)[] | null; description?: string; positive?: boolean }[] | undefined, page: number) {
  const boxes: { box: Box; label: string; positive: boolean }[] = [];
  for (const item of evidence ?? []) {
    if (Number(item.page) !== page || !item.bBox || item.bBox.length !== 4) continue;
    const [x0, y0, x1, y1] = item.bBox.map(Number) as [number, number, number, number];
    boxes.push({ box: { x0, y0, x1, y1 }, label: item.description ?? "", positive: item.positive !== false });
  }
  return boxes;
}

/** The evidence of the layout that identified the document (`appliedConfig.layout.identification`, docs/11 §8.2). */
export function appliedEvidence(appliedConfig: unknown) {
  const layout = (appliedConfig as { layout?: { identification?: unknown } } | null | undefined)?.layout;
  return Array.isArray(layout?.identification) ? (layout.identification as { page?: number; bBox?: number[]; description?: string }[]) : [];
}

/** The layout that filled the document, from `appliedConfig.layout` (key and version), or null. */
export function appliedLayout(appliedConfig: unknown): { layoutId: string; layoutKey: string; version: number } | null {
  const layout = (appliedConfig as { layout?: { layoutId?: string; layoutKey?: string; version?: number } } | null | undefined)?.layout;
  return layout?.layoutKey && layout.layoutId ? { layoutId: layout.layoutId, layoutKey: layout.layoutKey, version: Number(layout.version ?? 0) } : null;
}

/** Where each pre-filled value came from (`appliedConfig.prefill.sources`, docs/11 §12.10 step 4), and whether the LLM filled the rest. */
export function prefillOf(appliedConfig: unknown): { sources: Record<string, string>; filledByLlm: boolean; basisVersionId: string | null } | null {
  const prefill = (appliedConfig as { prefill?: { sources?: Record<string, string>; filledByLlm?: boolean; basisVersionId?: string | null } } | null | undefined)?.prefill;
  if (!prefill) return null;
  return { sources: prefill.sources ?? {}, filledByLlm: prefill.filledByLlm === true, basisVersionId: prefill.basisVersionId ?? null };
}

/** The suggestions a NEW_LAYOUT flag carries (`suggestions`, a JSON array of the suggester's results). */
export function flagSuggestions(flag: DocumentFlag | null): { layoutId: string; layoutKey: string; documentTypeKey: string; similarity: number }[] {
  const raw = flag?.params?.suggestions;
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw) as unknown;
    return Array.isArray(parsed) ? (parsed as { layoutId: string; layoutKey: string; documentTypeKey: string; similarity: number }[]) : [];
  } catch {
    return [];
  }
}

/** A layout key from a supplier's name and a type (`nordwind-invoice`), with a number when the supplier has that key already. */
export function suggestLayoutKey(supplierName: string | null | undefined, typeKey: string, existing: readonly string[]): string {
  const slug = (text: string) =>
    text
      .normalize("NFKD")
      .replace(/[̀-ͯ]/g, "")
      .replace(/ß/g, "ss")
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, "-")
      .replace(/^-+|-+$/g, "");
  const supplier = slug((supplierName ?? "").split(/\s+/)[0] ?? "") || "supplier";
  const base = `${supplier}-${slug(typeKey) || "document"}`.slice(0, 56);
  if (!existing.includes(base)) return base;
  for (let n = 2; n < 100; n++) {
    const key = `${base}-${n}`;
    if (!existing.includes(key)) return key;
  }
  return `${base}-${Date.now() % 1000}`;
}

/** A layout key the API takes (`LayoutEndpoints.LayoutKeyPattern`): lower-case letters, digits and dashes, at most 64, not starting or ending with a dash. */
export function isLayoutKey(key: string): boolean {
  return /^[a-z0-9](?:[a-z0-9-]{0,62}[a-z0-9])?$/.test(key);
}

/** What "Approve and save as layout" sends (`learnLayout` of `:approve`, docs/11 §12.10 step 6). */
export interface LayoutSaveChoice {
  saveAsLayout: boolean;
  saveAs: "newLayout" | "newVersion";
  targetLayoutId: string | null;
  layoutKey: string;
  name: string;
  pendingLayoutVersionId: string | null;
  confirmNotRecognised: boolean;
}

export function learnLayoutRequest(choice: LayoutSaveChoice, draft: IdentificationDraft): LearnLayoutRequest | null {
  if (!choice.saveAsLayout) return null;
  if (choice.pendingLayoutVersionId) return { pendingLayoutVersionId: choice.pendingLayoutVersionId };
  const rule = ruleOf(draft);
  const request: LearnLayoutRequest = { saveAs: choice.saveAs };
  if (choice.saveAs === "newVersion") {
    request.targetLayoutId = choice.targetLayoutId;
    if (choice.confirmNotRecognised) request.confirmNotRecognised = true;
  } else {
    request.layoutKey = choice.layoutKey.trim() || null;
    if (choice.name.trim()) request.name = choice.name.trim();
  }
  if (rule) request.identification = rule as never;
  return request;
}

/** A version's agreement per field as a percentage, the lowest first (the replay matrix, docs/11 §8.3). */
export function replayRows(replay: ReplayReport | null | undefined): { path: string; agreement: number; hits: number; mismatches: number; missing: number }[] {
  return (replay?.fields ?? [])
    .map((f) => ({ path: f.path, agreement: Math.round(Number(f.agreement) * 100), hits: Number(f.hits), mismatches: Number(f.mismatches), missing: Number(f.missing) }))
    .sort((a, b) => a.agreement - b.agreement || a.path.localeCompare(b.path));
}

/** The version of a layout a card leads with: the Active one, else the newest. */
export function leadVersion(layout: SupplierLayout): SupplierLayoutVersionSummary | null {
  const active = layout.activeVersion == null ? null : layout.versions.find((v) => Number(v.version) === Number(layout.activeVersion));
  return active ?? layout.versions[0] ?? null;
}

/** "document 2 of the first 5" while a version is inside its warm-up, null after it. */
export function warmupLeft(version: Pick<SupplierLayoutVersionSummary, "reviewWarmup" | "warmupSeen">): { seen: number; of: number } | null {
  const of = Number(version.reviewWarmup);
  const seen = Number(version.warmupSeen);
  return of > 0 && seen < of ? { seen, of } : null;
}
