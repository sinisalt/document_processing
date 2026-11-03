import { useState, type DragEvent, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useWatch } from "react-hook-form";
import { ArrowRightLeft, GripVertical, Plus, X } from "lucide-react";
import { Button, DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuTrigger, Input, Label, cn } from "@docproc/ui";
import { ConfidenceBadge, FieldStatusBadge, SourceBadge, getByPath, isFieldValue } from "@docproc/app-shell";
import { FieldInput, type HighlightHandler } from "./FieldInput";
import { RepeatingGroupGrid } from "./RepeatingGroupGrid";
import { OTHER_SECTION, type FieldSpec, type FormLayout, type FormNode, type FormSection } from "../lib/formSpec";
import { REVIEW_NS, humanize, localized, useFieldLabel } from "../lib/labels";
import { appendEntry, moveValue, removeAt, setAttribute, type FieldCorrection } from "../lib/corrections";
import { currencyOf, fieldDomId, fieldOf, formName, rawValueOf, toEditable } from "../lib/values";

export interface DefinitionFormProps {
  layout: FormLayout;
  canonical: unknown;
  disabled: boolean;
  threshold?: number;
  onHighlight: HighlightHandler;
  /** The canonical paths flags name (rows and columns of a repeating group a flag points at are shown and marked). */
  flaggedPaths?: readonly string[];
  /**
   * Sends one structural correction (`append` or `remove` of a row, a list entry or an attribute), for a
   * definition-driven document; without it only values are edited.
   */
  onStructuralChange?: (correction: FieldCorrection) => void;
  /**
   * Offers "match fields" (docs/11 §12.3): a field's value is dragged onto another field, or moved there from its menu,
   * and sent as one `move` correction. Without it no value can be moved.
   */
  onMove?: (correction: FieldCorrection) => void;
  busy?: boolean;
}

/** The drag-and-drop type of a value chip: its field's path. */
export const MOVE_MIME = "application/x-docproc-field";

interface Context extends Omit<DefinitionFormProps, "layout"> {
  layout: FormLayout;
  label: ReturnType<typeof useFieldLabel>;
}

/**
 * The generated review form (docs/11 §12.2): the sections of a document type's form spec in order, each a
 * `SectionRenderer` of its nodes (fields, composites, groups, lists and attribute bags), with every repeating group
 * as a `RepeatingGroupGrid` below its section's fields. It is bound to the surrounding react-hook-form by the
 * canonical paths of `layout.specs` (see `toFormValues`).
 */
export function DefinitionForm(props: DefinitionFormProps) {
  const label = useFieldLabel(props.layout);
  const context: Context = { ...props, label };
  return (
    <div className="flex flex-col gap-6" data-type-key={props.layout.key} data-type-version={props.layout.version}>
      {props.layout.sections.map((section) => (
        <SectionRenderer key={section.id} section={section} context={context} />
      ))}
    </div>
  );
}

function SectionRenderer({ section, context }: { section: FormSection; context: Context }) {
  const { t, i18n } = useTranslation(REVIEW_NS);
  const plain = section.nodes.filter((node) => node.type !== "grid");
  const grids = section.nodes.filter((node) => node.type === "grid");
  const title = section.id === OTHER_SECTION ? t("form.otherSection") : (localized(section.label, i18n.language) ?? (t(`sections.${section.id}`, { defaultValue: "" }) || humanize(section.id)));
  // A section that is one party or one set of payment terms badges that field once, at the section's title.
  const single = plain.length === 1 && plain[0]!.type === "composite" && !plain[0]!.inline ? plain[0]! : null;
  const block = single?.type === "composite" ? fieldOf(context.canonical, { fieldPath: single.path }) : undefined;
  return (
    <>
      {plain.length > 0 && (
        <fieldset className="min-w-0" data-section={section.id}>
          <legend className="mb-2 flex flex-wrap items-center gap-2 text-sm font-semibold">
            {title}
            {block && <Badges path={single!.type === "composite" ? single!.path : ""} context={context} />}
          </legend>
          <div className="grid grid-cols-1 gap-x-4 gap-y-3 @lg:grid-cols-2">
            {single?.type === "composite" ? <CompositeMembers specs={single.specs} context={context} /> : plain.map((node) => <NodeRenderer key={nodeKey(node)} node={node} context={context} />)}
          </div>
        </fieldset>
      )}
      {grids.map((node) => (
        <GridSlot key={nodeKey(node)} node={node} context={context} />
      ))}
    </>
  );
}

function nodeKey(node: FormNode): string {
  return node.type === "field" ? node.spec.path : node.path;
}

function Badges({ path, context }: { path: string; context: Context }) {
  const field = fieldOf(context.canonical, { fieldPath: path });
  if (!field) return null;
  return (
    <span className="flex flex-wrap items-center gap-1 font-normal" data-path={path}>
      <FieldStatusBadge status={field.status} />
      <ConfidenceBadge confidence={field.confidence} threshold={context.threshold} />
      <SourceBadge source={field.source} />
    </span>
  );
}

function GridSlot({ node, context }: { node: FormNode; context: Context }) {
  if (node.type !== "grid") return null;
  return (
    <RepeatingGroupGrid
      node={node}
      layout={context.layout}
      canonical={context.canonical}
      disabled={context.disabled}
      threshold={context.threshold}
      onHighlight={context.onHighlight}
      flaggedPaths={context.flaggedPaths}
      onStructuralChange={context.layout.structural ? context.onStructuralChange : undefined}
      busy={context.busy}
    />
  );
}

/** One node inside a section's (or a group's) two-column grid. */
function NodeRenderer({ node, context }: { node: FormNode; context: Context }) {
  const { t, i18n } = useTranslation(REVIEW_NS);
  switch (node.type) {
    case "field":
      return <FieldCell spec={node.spec} context={context} />;
    case "composite":
      if (node.inline) return <CompositeMembers specs={node.specs} context={context} />;
      return (
        <fieldset className="min-w-0 rounded-md border p-3 @lg:col-span-2" data-composite={node.path}>
          <legend className="flex flex-wrap items-center gap-2 px-1 text-sm font-medium">
            {context.label(node.path)}
            <Badges path={node.path} context={context} />
          </legend>
          <div className="grid grid-cols-1 gap-x-4 gap-y-3 @lg:grid-cols-2">
            <CompositeMembers specs={node.specs} context={context} />
          </div>
        </fieldset>
      );
    case "group":
      return (
        <fieldset className="min-w-0 rounded-md border p-3 @lg:col-span-2" data-group={node.path}>
          <legend className="px-1 text-sm font-medium">{localized(node.field.label, i18n.language) ?? humanize(node.field.key)}</legend>
          <div className="grid grid-cols-1 gap-x-4 gap-y-3 @lg:grid-cols-2">
            {node.nodes.filter((child) => child.type !== "grid").map((child) => (
              <NodeRenderer key={nodeKey(child)} node={child} context={context} />
            ))}
          </div>
          {node.nodes.filter((child) => child.type === "grid").map((child) => (
            <div key={nodeKey(child)} className="mt-3">
              <GridSlot node={child} context={context} />
            </div>
          ))}
        </fieldset>
      );
    case "list":
      return <ListField node={node} context={context} />;
    case "bag":
      return <BagField node={node} context={context} />;
    case "grid":
      return <div className="@lg:col-span-2">{<GridSlot node={node} context={context} />}</div>;
    default:
      void t;
      return null;
  }
}

function CompositeMembers({ specs, context }: { specs: FieldSpec[]; context: Context }) {
  return (
    <>
      {specs.map((spec) => (
        <FieldCell key={spec.path} spec={spec} context={context} />
      ))}
    </>
  );
}

/** Whether a value can be moved from or to this spec: a whole field outside a repeating group. */
function movable(spec: FieldSpec): boolean {
  return spec.path === spec.fieldPath && !spec.readOnly && !spec.path.includes("[");
}

/** The value chip of a field ("match fields", docs/11 §12.3): drag it onto another field, or pick that field from its menu. */
function MoveChip({ spec, context }: { spec: FieldSpec; context: Context }) {
  const { t } = useTranslation(REVIEW_NS);
  const targets = context.layout.specs.filter((other) => other.path !== spec.path && movable(other));
  if (targets.length === 0) return null;
  // Two elements: the handle is dragged (a menu trigger opens on pointer down, which would cover the drop target),
  // the button opens the menu, which is also the way without a mouse.
  return (
    <DropdownMenu>
      <span
        draggable={!context.busy}
        onDragStart={(event) => {
          event.dataTransfer.setData(MOVE_MIME, spec.path);
          event.dataTransfer.effectAllowed = "move";
        }}
        className="inline-flex h-4 cursor-grab items-center rounded text-muted-foreground hover:bg-muted hover:text-foreground"
        title={t("move.drag", { field: context.label(spec) })}
        aria-hidden
        data-testid={`drag-${spec.path}`}
      >
        <GripVertical className="size-3.5" />
      </span>
      <DropdownMenuTrigger asChild>
        <button
          type="button"
          className="inline-flex h-4 items-center rounded text-muted-foreground hover:bg-muted hover:text-foreground disabled:opacity-50"
          aria-label={t("move.chip", { field: context.label(spec) })}
          title={t("move.chip", { field: context.label(spec) })}
          disabled={context.busy}
          data-testid={`move-${spec.path}`}
        >
          <ArrowRightLeft className="size-3.5" aria-hidden />
        </button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="start" className="max-h-80 overflow-y-auto">
        <DropdownMenuLabel>{t("move.to")}</DropdownMenuLabel>
        {targets.map((target) => (
          <DropdownMenuItem key={target.path} onSelect={() => context.onMove?.(moveValue(spec.path, target.path))} data-testid={`move-target-${target.path}`}>
            {context.label(target)}
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}

/** A field with its label; hidden while its `visibleWhen` does not hold and it has no value. */
function FieldCell({ spec, context, trailing }: { spec: FieldSpec; context: Context; trailing?: ReactNode }) {
  const { t } = useTranslation(REVIEW_NS);
  const [dropping, setDropping] = useState(false);
  const field = fieldOf(context.canonical, spec);
  const wide = spec.kind === "text";
  const canMove = !!context.onMove && !context.disabled && movable(spec);
  const hasValue = !!field && field.status !== "Missing";
  const dropTarget = canMove
    ? {
        onDragOver: (event: DragEvent) => {
          if (!event.dataTransfer.types.includes(MOVE_MIME)) return;
          event.preventDefault();
          event.dataTransfer.dropEffect = "move";
          setDropping(true);
        },
        onDragLeave: () => setDropping(false),
        onDrop: (event: DragEvent) => {
          setDropping(false);
          const from = event.dataTransfer.getData(MOVE_MIME);
          if (!from || from === spec.path) return;
          event.preventDefault();
          context.onMove?.(moveValue(from, spec.path));
        },
      }
    : {};
  const cell = (
    <div className={cn(wide && "@lg:col-span-2", dropping && "rounded-md outline-2 outline-offset-2 outline-blue-500 outline-dashed")} data-drop-target={canMove || undefined} {...dropTarget}>
      <div className="mb-1 flex items-baseline gap-1">
        <Label htmlFor={fieldDomId(spec.path)} className="text-xs text-muted-foreground">
          {context.label(spec)}
        </Label>
        {spec.required && (
          <span className="text-xs text-destructive" title={t("form.required")} aria-hidden>
            *
          </span>
        )}
        {canMove && hasValue && <MoveChip spec={spec} context={context} />}
      </div>
      <FieldInput
        spec={spec}
        field={field}
        currency={spec.kind === "money" ? currencyOf(context.canonical, spec) : undefined}
        disabled={context.disabled}
        threshold={context.threshold}
        onHighlight={context.onHighlight}
        showBadges={spec.path === spec.fieldPath}
        trailing={trailing}
      />
    </div>
  );
  if (!spec.visibleWhen) return cell;
  return (
    <VisibleWhen spec={spec} canonical={context.canonical}>
      {cell}
    </VisibleWhen>
  );
}

function VisibleWhen({ spec, canonical, children }: { spec: FieldSpec; canonical: unknown; children: ReactNode }) {
  const condition = spec.visibleWhen!;
  // The value as typed: switching an invoice to a credit note shows its original invoice reference at once.
  const watched = useWatch({ name: formName(condition.path) });
  const stored = rawValueOf(canonical, spec);
  const holds = String(watched ?? "") === String(condition.equals ?? "");
  // A field that holds a value is never hidden: its value is still saved, so it must be seen.
  if (!holds && toEditable(spec.kind, stored) === "") return null;
  return <>{children}</>;
}

function ListField({ node, context }: { node: Extract<FormNode, { type: "list" }>; context: Context }) {
  const { t } = useTranslation(REVIEW_NS);
  const [draft, setDraft] = useState("");
  const canEdit = !context.disabled && context.layout.structural && !!context.onStructuralChange && !node.field.readOnly;
  const name = context.label(node.path);
  const addId = `${fieldDomId(node.path)}-new`;
  return (
    <>
      {node.entries.map((spec) => (
        <FieldCell
          key={spec.path}
          spec={spec}
          context={context}
          trailing={
            canEdit ? (
              <Button type="button" variant="ghost" size="icon-sm" aria-label={t("list.remove", { field: context.label(spec, true) })} disabled={context.busy} onClick={() => context.onStructuralChange?.(removeAt(spec.path))}>
                <X aria-hidden />
              </Button>
            ) : undefined
          }
        />
      ))}
      {canEdit && (
        <div data-list={node.path}>
          <Label htmlFor={addId} className="mb-1 text-xs text-muted-foreground">
            {t("list.newEntry", { field: name })}
          </Label>
          <div className="flex items-center gap-1">
            <Input id={addId} value={draft} onChange={(event) => setDraft(event.target.value)} placeholder={node.entry.kind === "date" ? "yyyy-mm-dd" : undefined} />
            <Button
              type="button"
              variant="outline"
              size="sm"
              disabled={context.busy || draft.trim() === ""}
              onClick={() => {
                context.onStructuralChange?.(appendEntry(node.path, node.entry.kind, draft));
                setDraft("");
              }}
            >
              <Plus aria-hidden />
              {t("list.add")}
            </Button>
          </div>
        </div>
      )}
    </>
  );
}

const ATTRIBUTE_KEY = /^[A-Za-z_][A-Za-z0-9_]*$/;

function BagField({ node, context }: { node: Extract<FormNode, { type: "bag" }>; context: Context }) {
  const { t, i18n } = useTranslation(REVIEW_NS);
  const [key, setKey] = useState("");
  const [value, setValue] = useState("");
  const canEdit = !context.disabled && context.layout.structural && !!context.onStructuralChange && !node.field.readOnly;
  const stored = getByPath(context.canonical, node.path);
  const taken = new Set(stored && typeof stored === "object" ? Object.keys(stored as object) : []);
  const keyInvalid = key.trim() !== "" && (!ATTRIBUTE_KEY.test(key.trim()) || taken.has(key.trim()));
  const id = fieldDomId(node.path);
  const title = localized(node.field.label, i18n.language) ?? humanize(node.field.key);
  return (
    <fieldset className="min-w-0 rounded-md border p-3 @lg:col-span-2" data-bag={node.path}>
      <legend className="px-1 text-sm font-medium">{title}</legend>
      <div className="grid grid-cols-1 gap-x-4 gap-y-3 @lg:grid-cols-2">
        {node.entries.length === 0 && <p className="text-sm text-muted-foreground">{t("bag.empty")}</p>}
        {node.entries.map((spec) => (
          <FieldCell
            key={spec.path}
            spec={spec}
            context={context}
            trailing={
              canEdit && isFieldValue(getByPath(context.canonical, spec.path)) ? (
                <Button type="button" variant="ghost" size="icon-sm" aria-label={t("bag.remove", { key: context.label(spec) })} disabled={context.busy} onClick={() => context.onStructuralChange?.(removeAt(spec.path))}>
                  <X aria-hidden />
                </Button>
              ) : undefined
            }
          />
        ))}
      </div>
      {canEdit && (
        <div className="mt-3 flex flex-wrap items-end gap-2">
          <div className="flex flex-col gap-1">
            <Label htmlFor={`${id}-key`} className="text-xs text-muted-foreground">
              {t("bag.key")}
            </Label>
            <Input id={`${id}-key`} value={key} onChange={(event) => setKey(event.target.value)} className="w-40" aria-invalid={keyInvalid || undefined} aria-describedby={keyInvalid ? `${id}-key-error` : undefined} />
          </div>
          <div className="flex flex-col gap-1">
            <Label htmlFor={`${id}-value`} className="text-xs text-muted-foreground">
              {t("bag.value")}
            </Label>
            <Input id={`${id}-value`} value={value} onChange={(event) => setValue(event.target.value)} className="w-48" />
          </div>
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={context.busy || keyInvalid || key.trim() === "" || value.trim() === ""}
            onClick={() => {
              context.onStructuralChange?.(setAttribute(`${node.path}.${key.trim()}`, value));
              setKey("");
              setValue("");
            }}
          >
            <Plus aria-hidden />
            {t("bag.add")}
          </Button>
          {keyInvalid && (
            <p id={`${id}-key-error`} role="alert" className="w-full text-xs text-destructive">
              {t("bag.invalidKey")}
            </p>
          )}
        </div>
      )}
    </fieldset>
  );
}
