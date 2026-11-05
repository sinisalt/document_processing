import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ChevronDown, ChevronRight, List } from "lucide-react";
import { Badge, Button, Collapsible, CollapsibleContent, CollapsibleTrigger, Input } from "@docproc/ui";
import { listSample } from "./fieldMap";

/** What a pick in the source tree names: the full path, and the list it lies in (`doc.lines`) when it is inside one. */
export interface SourcePick {
  path: string;
  /** For a field inside a list: the list's path and the field's path relative to its row. */
  list?: { path: string; relative: string };
}

/** The top-level blocks of the values view in the order docs/11 §13 names them. */
const BLOCK_ORDER = ["doc", "roles", "supplier", "entity", "source", "meta", "links"];

function sample(value: unknown): string {
  if (value === null) return "null";
  if (value === undefined) return "";
  const text = typeof value === "string" ? `"${value}"` : String(value);
  return text.length > 40 ? `${text.slice(0, 37)}…` : text;
}

/**
 * The source pane of the mapping editor (docs/11 §19): the values view of the chosen document (`doc` with the type's
 * fields, `roles`, `supplier`, `entity`, `source`, `meta`, `links`) as a tree with its values. A click on a field puts its
 * path into the row being edited; a list (`doc.lines`, 412 rows) shows the fields of its rows, picked relative to the row.
 */
export function SourceTree({ values, onPick }: { values: unknown; onPick?: (pick: SourcePick) => void }) {
  const { t } = useTranslation();
  const [filter, setFilter] = useState("");
  if (!values || typeof values !== "object") return <p className="text-sm text-muted-foreground">{t("integrations.editor.noValues")}</p>;
  const entries = Object.entries(values as Record<string, unknown>).sort(([a], [b]) => (BLOCK_ORDER.indexOf(a) + 1 || 99) - (BLOCK_ORDER.indexOf(b) + 1 || 99));
  return (
    <div className="flex flex-col gap-2" data-testid="source-tree">
      <Input value={filter} onChange={(event) => setFilter(event.target.value)} placeholder={t("integrations.editor.filterFields")} className="h-8" aria-label={t("integrations.editor.filterFields")} />
      <ul className="flex flex-col gap-0.5 text-xs">
        {entries.map(([key, value]) => (
          <TreeNode key={key} name={key} path={key} value={value} root={values} depth={0} filter={filter.trim().toLowerCase()} onPick={onPick} defaultOpen={key === "doc" || key === "roles"} />
        ))}
      </ul>
    </div>
  );
}

interface NodeProps {
  name: string;
  path: string;
  value: unknown;
  root: unknown;
  depth: number;
  filter: string;
  onPick?: (pick: SourcePick) => void;
  defaultOpen?: boolean;
  /** Inside a list: the list's path and this node's path relative to its row. */
  list?: { path: string; relative: string };
}

function matches(path: string, value: unknown, filter: string): boolean {
  if (!filter) return true;
  if (path.toLowerCase().includes(filter)) return true;
  if (value && typeof value === "object") return Object.entries(value as Record<string, unknown>).some(([key, child]) => matches(`${path}.${key}`, Array.isArray(child) ? {} : child, filter));
  return false;
}

function TreeNode({ name, path, value, root, depth, filter, onPick, defaultOpen, list }: NodeProps) {
  const { t } = useTranslation();
  const [open, setOpen] = useState(defaultOpen ?? false);
  const pickable = onPick !== undefined;
  if (!matches(path, Array.isArray(value) ? {} : value, filter) && !path.toLowerCase().includes(filter)) return null;
  const pick = () => onPick?.({ path, list });

  if (Array.isArray(value)) {
    // The fields of the list's rows, from its first rows (a row without tiers does not hide the tier fields).
    const rowSample = list ? listSample({ x: value }, "x") : listSample(root, path);
    return (
      <li>
        <Collapsible open={open || !!filter} onOpenChange={setOpen}>
          <div className="flex items-center gap-1" style={{ paddingLeft: depth * 12 }}>
            <CollapsibleTrigger asChild>
              <Button type="button" variant="ghost" size="icon" className="size-5" aria-label={open ? t("integrations.editor.collapse") : t("integrations.editor.expand")}>
                {open || filter ? <ChevronDown aria-hidden /> : <ChevronRight aria-hidden />}
              </Button>
            </CollapsibleTrigger>
            <Button type="button" variant="link" size="xs" className="h-auto p-0 text-xs text-foreground flex items-center gap-1 font-mono" onClick={pick} disabled={!pickable} data-source-path={path}>
              <List className="size-3" aria-hidden />
              {name}
            </Button>
            <Badge variant="outline" className="h-4 px-1 text-[10px]" data-testid={`source-count-${path}`}>
              {t("integrations.editor.rows", { count: value.length })}
            </Badge>
          </div>
          <CollapsibleContent>
            <ul className="flex flex-col gap-0.5">
              {rowSample && typeof rowSample === "object"
                ? Object.entries(rowSample as Record<string, unknown>).map(([key, child]) => (
                    <TreeNode key={key} name={key} path={`${path}[].${key}`} value={child} root={root} depth={depth + 1} filter={filter} onPick={onPick} list={{ path: list ? list.path : path, relative: list ? `${list.relative}[].${key}` : key }} />
                  ))
                : null}
            </ul>
          </CollapsibleContent>
        </Collapsible>
      </li>
    );
  }

  if (value && typeof value === "object") {
    return (
      <li>
        <Collapsible open={open || !!filter} onOpenChange={setOpen}>
          <div className="flex items-center gap-1" style={{ paddingLeft: depth * 12 }}>
            <CollapsibleTrigger asChild>
              <Button type="button" variant="ghost" size="icon" className="size-5" aria-label={open ? t("integrations.editor.collapse") : t("integrations.editor.expand")}>
                {open || filter ? <ChevronDown aria-hidden /> : <ChevronRight aria-hidden />}
              </Button>
            </CollapsibleTrigger>
            <Button type="button" variant="link" size="xs" className="h-auto p-0 text-xs text-foreground font-mono" onClick={pick} disabled={!pickable} data-source-path={path}>
              {name}
            </Button>
          </div>
          <CollapsibleContent>
            <ul className="flex flex-col gap-0.5">
              {Object.entries(value as Record<string, unknown>).map(([key, child]) => (
                <TreeNode key={key} name={key} path={`${path}.${key}`} value={child} root={root} depth={depth + 1} filter={filter} onPick={onPick} list={list ? { path: list.path, relative: `${list.relative}.${key}` } : undefined} />
              ))}
            </ul>
          </CollapsibleContent>
        </Collapsible>
      </li>
    );
  }

  return (
    <li className="flex items-center gap-2" style={{ paddingLeft: depth * 12 + 24 }}>
      <Button type="button" variant="link" size="xs" className="h-auto p-0 text-xs text-foreground font-mono" onClick={pick} disabled={!pickable} data-source-path={path} title={pickable ? t("integrations.editor.pickHint") : undefined}>
        {name}
      </Button>
      <span className="truncate text-muted-foreground">{sample(value)}</span>
    </li>
  );
}
