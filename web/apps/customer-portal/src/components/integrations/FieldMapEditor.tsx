import { useTranslation } from "react-i18next";
import { ArrowDown, ArrowUp, Plus, Settings2, Trash2 } from "lucide-react";
import { Badge, Button, Input, Label, Popover, PopoverContent, PopoverTrigger, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, cn } from "@docproc/ui";
import { appendRow, moveRow, updateRowAt, type FieldMap, type FieldMapProblem, type Formatter, type Literal, type MapRow, type ValueRow } from "./fieldMap";

const kinds = ["value", "constant", "expression", "list"] as const;
const literalTypes = ["text", "number", "boolean", "null"] as const;
const formatKinds = ["none", "date", "number", "truncate"] as const;
const NONE = "__none";

export interface FieldMapEditorProps {
  map: FieldMap;
  onChange: (map: FieldMap) => void;
  /** The row whose source a pick in the source tree fills. */
  activeRow: string | null;
  onActiveRowChange: (at: string) => void;
  problems: FieldMapProblem[];
  /** The tenant's and the platform's lookup tables, for the lookup option. */
  lookupTables: string[];
  readOnly: boolean;
}

/** A new row of a kind, keeping the target of the row it replaces. */
function rowOfKind(kind: (typeof kinds)[number], target: string): MapRow {
  switch (kind) {
    case "value":
      return { kind, target, source: "" };
    case "constant":
      return { kind, target, value: { type: "text", value: "" } };
    case "expression":
      return { kind, target, expression: "" };
    case "list":
      return { kind, target, source: "", rows: [{ kind: "value", target: "", source: "" }] };
  }
}

/**
 * The visual field map (docs/11 §13.1, §19): one line per output field, target path on the left ("header.vendorNo"
 * builds nested objects), its source on the right: a field of the values view (with a second field when empty, a
 * lookup table, a formatter, a constant when empty, a required check), a constant, an expression of its own, or a
 * list that writes one object per row of a list of the view (with a filter), whose rows read the row's fields.
 */
export function FieldMapEditor({ map, onChange, activeRow, onActiveRowChange, problems, lookupTables, readOnly }: FieldMapEditorProps) {
  const { t } = useTranslation();
  const set = (rows: MapRow[]) => onChange({ ...map, rows });
  return (
    <div className="flex flex-col gap-2" data-testid="field-map">
      <div className="hidden grid-cols-[7.5rem_minmax(0,1fr)_minmax(0,1.2fr)_auto] gap-2 px-1 text-xs font-medium text-muted-foreground md:grid">
        <span>{t("integrations.fieldMap.kind")}</span>
        <span>{t("integrations.fieldMap.target")}</span>
        <span>{t("integrations.fieldMap.source")}</span>
        <span />
      </div>
      <Rows rows={map.rows} prefix="" depth={0} all={map.rows} set={set} activeRow={activeRow} onActiveRowChange={onActiveRowChange} problems={problems} lookupTables={lookupTables} readOnly={readOnly} scalarAllowed={false} />
      {!readOnly && (
        <div className="flex gap-2">
          <Button type="button" size="sm" variant="outline" onClick={() => set(appendRow(map.rows, null, rowOfKind("value", "")))} data-testid="fm-add">
            <Plus aria-hidden />
            {t("integrations.fieldMap.addField")}
          </Button>
          <Button type="button" size="sm" variant="outline" onClick={() => set(appendRow(map.rows, null, rowOfKind("list", "")))} data-testid="fm-add-list">
            <Plus aria-hidden />
            {t("integrations.fieldMap.addList")}
          </Button>
        </div>
      )}
    </div>
  );
}

interface RowsProps {
  rows: MapRow[];
  prefix: string;
  depth: number;
  all: MapRow[];
  set: (rows: MapRow[]) => void;
  activeRow: string | null;
  onActiveRowChange: (at: string) => void;
  problems: FieldMapProblem[];
  lookupTables: string[];
  readOnly: boolean;
  /** Inside a list: a single row with an empty target makes it a list of plain values. */
  scalarAllowed: boolean;
}

function Rows(props: RowsProps) {
  return (
    <ul className="flex flex-col gap-1.5">
      {props.rows.map((row, i) => (
        <RowEditor key={`${props.prefix}${i}`} {...props} row={row} at={`${props.prefix}${i}`} index={i} count={props.rows.length} />
      ))}
    </ul>
  );
}

function RowEditor({ row, at, index, count, depth, all, set, activeRow, onActiveRowChange, problems, lookupTables, readOnly, scalarAllowed }: RowsProps & { row: MapRow; at: string; index: number; count: number }) {
  const { t } = useTranslation();
  const update = (next: MapRow) => set(updateRowAt(all, at, () => next));
  const own = problems.filter((p) => p.at === at);
  const active = activeRow === at;
  return (
    <li className={cn("rounded-md border p-1.5", active && "border-primary", own.length > 0 && "border-destructive/60")} data-testid={`fm-row-${at}`} data-kind={row.kind} onFocusCapture={() => onActiveRowChange(at)}>
      <div className="grid grid-cols-1 items-start gap-2 md:grid-cols-[7.5rem_minmax(0,1fr)_minmax(0,1.2fr)_auto]">
        <Select value={row.kind} onValueChange={(kind) => update(rowOfKind(kind as (typeof kinds)[number], row.target))} disabled={readOnly}>
          <SelectTrigger className="h-8" data-testid={`fm-kind-${at}`} aria-label={t("integrations.fieldMap.kind")}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {kinds.map((kind) => (
              <SelectItem key={kind} value={kind}>
                {t(`integrations.fieldMap.kinds.${kind}`)}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <Input
          value={row.target}
          onChange={(event) => update({ ...row, target: event.target.value })}
          placeholder={scalarAllowed && count === 1 ? t("integrations.fieldMap.targetScalar") : t("integrations.fieldMap.targetPlaceholder")}
          className="h-8 font-mono text-xs"
          readOnly={readOnly}
          aria-label={t("integrations.fieldMap.target")}
          data-testid={`fm-target-${at}`}
        />
        <SourceCell row={row} at={at} update={update} readOnly={readOnly} />
        <div className="flex items-center gap-0.5">
          {row.kind === "value" && <ValueOptions row={row} at={at} update={update} lookupTables={lookupTables} readOnly={readOnly} />}
          {!readOnly && (
            <>
              <Button type="button" size="icon" variant="ghost" className="size-7" disabled={index === 0} onClick={() => set(moveRow(all, at, -1))} aria-label={t("integrations.fieldMap.moveUp")}>
                <ArrowUp aria-hidden />
              </Button>
              <Button type="button" size="icon" variant="ghost" className="size-7" disabled={index === count - 1} onClick={() => set(moveRow(all, at, 1))} aria-label={t("integrations.fieldMap.moveDown")}>
                <ArrowDown aria-hidden />
              </Button>
              <Button type="button" size="icon" variant="ghost" className="size-7" onClick={() => set(updateRowAt(all, at, () => null))} aria-label={t("integrations.fieldMap.remove")} data-testid={`fm-remove-${at}`}>
                <Trash2 aria-hidden />
              </Button>
            </>
          )}
        </div>
      </div>
      {row.kind === "value" && <ValueSummary row={row} />}
      {own.length > 0 && (
        <ul className="mt-1 px-1 text-xs text-destructive" data-testid={`fm-problems-${at}`}>
          {own.map((problem) => (
            <li key={problem.code}>{t(`integrations.fieldMap.problems.${problem.code}`)}</li>
          ))}
        </ul>
      )}
      {row.kind === "list" && (
        <div className="mt-2 flex flex-col gap-1.5 border-l-2 border-muted pl-3" style={{ marginLeft: Math.min(depth, 3) * 4 }}>
          <div className="flex items-center gap-2">
            <Label htmlFor={`fm-filter-${at}`} className="shrink-0 text-xs text-muted-foreground">
              {t("integrations.fieldMap.filter")}
            </Label>
            <Input id={`fm-filter-${at}`} value={row.filter ?? ""} onChange={(event) => update({ ...row, filter: event.target.value || undefined })} placeholder={'$not(status = "Discontinued")'} className="h-8 font-mono text-xs" readOnly={readOnly} data-testid={`fm-filter-${at}`} />
          </div>
          <Rows rows={row.rows} prefix={`${at}.`} depth={depth + 1} all={all} set={set} activeRow={activeRow} onActiveRowChange={onActiveRowChange} problems={problems} lookupTables={lookupTables} readOnly={readOnly} scalarAllowed />
          {!readOnly && (
            <div className="flex gap-2">
              <Button type="button" size="xs" variant="ghost" onClick={() => set(appendRow(all, at, rowOfKind("value", "")))} data-testid={`fm-add-${at}`}>
                <Plus aria-hidden />
                {t("integrations.fieldMap.addFieldInList")}
              </Button>
              <Button type="button" size="xs" variant="ghost" onClick={() => set(appendRow(all, at, rowOfKind("list", "")))} data-testid={`fm-add-list-${at}`}>
                <Plus aria-hidden />
                {t("integrations.fieldMap.addListInList")}
              </Button>
            </div>
          )}
        </div>
      )}
    </li>
  );
}

function SourceCell({ row, at, update, readOnly }: { row: MapRow; at: string; update: (row: MapRow) => void; readOnly: boolean }) {
  const { t } = useTranslation();
  switch (row.kind) {
    case "value":
    case "list":
      return (
        <Input
          value={row.source}
          onChange={(event) => update({ ...row, source: event.target.value })}
          placeholder={row.kind === "list" ? "doc.lines" : t("integrations.fieldMap.sourcePlaceholder")}
          className="h-8 font-mono text-xs"
          readOnly={readOnly}
          aria-label={t("integrations.fieldMap.source")}
          data-testid={`fm-source-${at}`}
        />
      );
    case "constant":
      return <LiteralInput value={row.value} onChange={(value) => update({ ...row, value })} readOnly={readOnly} testId={`fm-constant-${at}`} />;
    case "expression":
      return (
        <Input
          value={row.expression}
          onChange={(event) => update({ ...row, expression: event.target.value })}
          placeholder={'doc.priceBasis = "Gross" ? "B" : "N"'}
          className="h-8 font-mono text-xs"
          readOnly={readOnly}
          aria-label={t("integrations.fieldMap.expression")}
          data-testid={`fm-expression-${at}`}
        />
      );
  }
}

function LiteralInput({ value, onChange, readOnly, testId }: { value: Literal; onChange: (value: Literal) => void; readOnly: boolean; testId: string }) {
  const { t } = useTranslation();
  return (
    <div className="flex gap-1">
      <Select value={value.type} onValueChange={(type) => onChange({ type: type as Literal["type"], value: type === "boolean" ? "true" : value.value })} disabled={readOnly}>
        <SelectTrigger className="h-8 w-28 shrink-0" data-testid={`${testId}-type`} aria-label={t("integrations.fieldMap.literalType")}>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          {literalTypes.map((type) => (
            <SelectItem key={type} value={type}>
              {t(`integrations.fieldMap.literalTypes.${type}`)}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
      {value.type === "boolean" ? (
        <Select value={value.value === "true" ? "true" : "false"} onValueChange={(v) => onChange({ ...value, value: v })} disabled={readOnly}>
          <SelectTrigger className="h-8" data-testid={testId}>
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="true">true</SelectItem>
            <SelectItem value="false">false</SelectItem>
          </SelectContent>
        </Select>
      ) : value.type !== "null" ? (
        <Input value={value.value} onChange={(event) => onChange({ ...value, value: event.target.value })} className="h-8 font-mono text-xs" readOnly={readOnly} data-testid={testId} aria-label={t("integrations.fieldMap.value")} />
      ) : null}
    </div>
  );
}

/** The options of a value row in words, under the row: "or $root.roles.validFrom · lookup erp-uom (ST) · date yyyyMMdd". */
function ValueSummary({ row }: { row: ValueRow }) {
  const { t } = useTranslation();
  const parts: string[] = [];
  if (row.fallback) parts.push(t("integrations.fieldMap.summary.fallback", { path: row.fallback }));
  if (row.lookup) parts.push(t("integrations.fieldMap.summary.lookup", { table: row.lookup.table, value: row.lookup.default ?? "—" }));
  if (row.format?.kind === "date") parts.push(t("integrations.fieldMap.summary.date", { pattern: row.format.pattern }));
  if (row.format?.kind === "number") parts.push(t("integrations.fieldMap.summary.number", { decimals: row.format.decimals }));
  if (row.format?.kind === "truncate") parts.push(t("integrations.fieldMap.summary.truncate", { length: row.format.length }));
  if (row.whenEmpty) parts.push(t("integrations.fieldMap.summary.whenEmpty", { value: row.whenEmpty.type === "text" ? `"${row.whenEmpty.value}"` : row.whenEmpty.type === "null" ? "null" : row.whenEmpty.value }));
  if (row.required) parts.push(t("integrations.fieldMap.summary.required"));
  if (parts.length === 0) return null;
  return (
    <div className="mt-1 flex flex-wrap gap-1 px-1">
      {parts.map((part) => (
        <Badge key={part} variant="secondary" className="font-mono text-[10px] font-normal">
          {part}
        </Badge>
      ))}
    </div>
  );
}

function formatterOf(kind: (typeof formatKinds)[number], previous?: Formatter): Formatter | undefined {
  switch (kind) {
    case "date":
      return { kind, pattern: previous?.kind === "date" ? previous.pattern : "yyyy-MM-dd" };
    case "number":
      return { kind, decimals: previous?.kind === "number" ? previous.decimals : 2 };
    case "truncate":
      return { kind, length: previous?.kind === "truncate" ? previous.length : 40 };
    default:
      return undefined;
  }
}

function ValueOptions({ row, at, update, lookupTables, readOnly }: { row: ValueRow; at: string; update: (row: MapRow) => void; lookupTables: string[]; readOnly: boolean }) {
  const { t } = useTranslation();
  const set = (patch: Partial<ValueRow>) => {
    const next: ValueRow = { ...row, ...patch };
    for (const key of Object.keys(patch) as Array<keyof ValueRow>) if (next[key] === undefined) delete next[key];
    update(next);
  };
  const tables = Array.from(new Set([...lookupTables, ...(row.lookup?.table ? [row.lookup.table] : [])])).sort();
  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button type="button" size="icon" variant="ghost" className="size-7" aria-label={t("integrations.fieldMap.options")} data-testid={`fm-options-${at}`}>
          <Settings2 aria-hidden />
        </Button>
      </PopoverTrigger>
      <PopoverContent className="grid w-80 gap-3" align="end" data-testid={`fm-options-panel-${at}`}>
        <div className="grid gap-1">
          <Label htmlFor={`fm-fallback-${at}`}>{t("integrations.fieldMap.fallback")}</Label>
          <Input id={`fm-fallback-${at}`} value={row.fallback ?? ""} onChange={(event) => set({ fallback: event.target.value || undefined })} placeholder="$root.roles.validFrom" className="h-8 font-mono text-xs" readOnly={readOnly} data-testid="fm-fallback" />
        </div>
        <div className="grid grid-cols-[1fr_6rem] gap-2">
          <div className="grid gap-1">
            <Label>{t("integrations.fieldMap.lookup")}</Label>
            <Select value={row.lookup?.table || NONE} onValueChange={(table) => set({ lookup: table === NONE ? undefined : { table, default: row.lookup?.default } })} disabled={readOnly}>
              <SelectTrigger className="h-8" data-testid="fm-lookup-table">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={NONE}>{t("integrations.fieldMap.noLookup")}</SelectItem>
                {tables.map((table) => (
                  <SelectItem key={table} value={table}>
                    {table}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <div className="grid gap-1">
            <Label htmlFor={`fm-lookup-default-${at}`}>{t("integrations.fieldMap.lookupDefault")}</Label>
            <Input id={`fm-lookup-default-${at}`} value={row.lookup?.default ?? ""} onChange={(event) => row.lookup && set({ lookup: { table: row.lookup.table, default: event.target.value || undefined } })} disabled={!row.lookup} className="h-8 font-mono text-xs" readOnly={readOnly} data-testid="fm-lookup-default" />
          </div>
        </div>
        <div className="grid grid-cols-[1fr_7rem] gap-2">
          <div className="grid gap-1">
            <Label>{t("integrations.fieldMap.format")}</Label>
            <Select value={row.format?.kind ?? "none"} onValueChange={(kind) => set({ format: formatterOf(kind as (typeof formatKinds)[number], row.format) })} disabled={readOnly}>
              <SelectTrigger className="h-8" data-testid="fm-format-kind">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {formatKinds.map((kind) => (
                  <SelectItem key={kind} value={kind}>
                    {t(`integrations.fieldMap.formats.${kind}`)}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          {row.format?.kind === "date" && (
            <div className="grid gap-1">
              <Label htmlFor={`fm-pattern-${at}`}>{t("integrations.fieldMap.pattern")}</Label>
              <Input id={`fm-pattern-${at}`} value={row.format.pattern} onChange={(event) => set({ format: { kind: "date", pattern: event.target.value } })} className="h-8 font-mono text-xs" readOnly={readOnly} data-testid="fm-format-pattern" />
            </div>
          )}
          {row.format?.kind === "number" && (
            <div className="grid gap-1">
              <Label htmlFor={`fm-decimals-${at}`}>{t("integrations.fieldMap.decimals")}</Label>
              <Input id={`fm-decimals-${at}`} type="number" min={0} max={10} value={row.format.decimals} onChange={(event) => set({ format: { kind: "number", decimals: Number(event.target.value) } })} className="h-8" readOnly={readOnly} data-testid="fm-format-decimals" />
            </div>
          )}
          {row.format?.kind === "truncate" && (
            <div className="grid gap-1">
              <Label htmlFor={`fm-length-${at}`}>{t("integrations.fieldMap.length")}</Label>
              <Input id={`fm-length-${at}`} type="number" min={1} value={row.format.length} onChange={(event) => set({ format: { kind: "truncate", length: Number(event.target.value) } })} className="h-8" readOnly={readOnly} data-testid="fm-format-length" />
            </div>
          )}
        </div>
        <div className="grid gap-1">
          <Label>{t("integrations.fieldMap.whenEmpty")}</Label>
          <div className="flex gap-1">
            <Select value={row.whenEmpty ? "set" : NONE} onValueChange={(v) => set({ whenEmpty: v === NONE ? undefined : (row.whenEmpty ?? { type: "text", value: "" }) })} disabled={readOnly}>
              <SelectTrigger className="h-8 w-24 shrink-0" data-testid="fm-when-empty-on">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={NONE}>{t("integrations.fieldMap.nothing")}</SelectItem>
                <SelectItem value="set">{t("integrations.fieldMap.constant")}</SelectItem>
              </SelectContent>
            </Select>
            {row.whenEmpty && <LiteralInput value={row.whenEmpty} onChange={(whenEmpty) => set({ whenEmpty })} readOnly={readOnly} testId="fm-when-empty" />}
          </div>
        </div>
        <div className="grid gap-1">
          <Label htmlFor={`fm-required-${at}`}>{t("integrations.fieldMap.required")}</Label>
          <Input id={`fm-required-${at}`} value={row.required ?? ""} onChange={(event) => set({ required: event.target.value || undefined })} placeholder={t("integrations.fieldMap.requiredPlaceholder")} className="h-8 text-xs" readOnly={readOnly} data-testid="fm-required" />
        </div>
      </PopoverContent>
    </Popover>
  );
}
