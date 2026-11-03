import { useRef, useState, type PointerEvent as ReactPointerEvent } from "react";
import { useTranslation } from "react-i18next";
import { cn } from "@docproc/ui";
import { boxFromDrag, isClick, wordAt, type Box } from "@docproc/review";
import { mapBox, toNormalized, type ProvenanceBox, type Size, type TextLayerWord } from "@/lib/overlay";

/** What a pointer on the page does: nothing special (boxes focus their fields), map mode, or the table tool. */
export type OverlayInteraction = "map" | "table" | null;

/** A pick on the page in page units: a word (by its index in the page's words) or a dragged box. */
export type PagePick = { kind: "word"; index: number; box: Box; additive: boolean } | { kind: "region"; box: Box };

/** The table tool's drawing on this page: the table's box, its dividers, the header band, rejected lines. */
export interface TableDrawing {
  box: Box;
  dividers: readonly number[];
  headerRow?: { y0: number; y1: number } | null;
  rejected?: readonly Box[];
  /** Moving a divider (drag, or arrow keys) and adding one (double-click inside the box); without them it is read-only. */
  onDividerMove?: (index: number, x: number) => void;
  onDividerAdd?: (x: number) => void;
}

export interface FieldHighlightOverlayProps {
  /** The rendered size of the page image in CSS pixels; the SVG covers exactly that area. */
  rendered: Size;
  /** Provenance boxes of this page (already filtered by page). */
  boxes: ProvenanceBox[];
  /** Word boxes of this page in the normalized space; drawn faintly when `showWords`, hit-tested in map mode. */
  words?: TextLayerWord[];
  showWords?: boolean;
  /** The field whose boxes are highlighted (hovered or focused in the form). */
  highlightedPath: string | null;
  onHover?: (path: string | null) => void;
  /** Clicking a box focuses its field in the form. */
  onSelect?: (path: string) => void;
  /** The field's label in the UI language for a box's tooltip and accessible name; the raw path without it. */
  labelOf?: (path: string) => string;
  className?: string;
  /**
   * Map mode or the table tool (docs/11 §12.3, §12.4): a click picks the word under it (shift-click adds it), a drag
   * draws a box. The boxes of the fields stay visible but take no pointer events then.
   */
  interaction?: OverlayInteraction;
  onPick?: (pick: PagePick) => void;
  /** The current selection (map mode), drawn in its own colour. */
  selection?: readonly Box[];
  table?: TableDrawing | null;
}

const rawPath = (path: string) => path;

/** One drawn box: the fields read from the same place on the page share it. */
interface SharedBox {
  box: ProvenanceBox["box"];
  historical: boolean;
  paths: string[];
}

/**
 * Groups boxes that cover the same place (and are both current or both history) into one. Every row a table mapping
 * wrote under a group heading carries that heading's box as its category's source, so a page held 30 boxes at the
 * same place; their translucent fills stacked into an opaque one that hid the heading.
 */
export function shareBoxes(boxes: readonly ProvenanceBox[]): SharedBox[] {
  const byPlace = new Map<string, SharedBox>();
  for (const { box, historical, path } of boxes) {
    const key = `${box.x0}|${box.y0}|${box.x1}|${box.y1}|${historical}`;
    const shared = byPlace.get(key);
    if (!shared) byPlace.set(key, { box, historical, paths: [path] });
    else if (!shared.paths.includes(path)) shared.paths.push(path);
  }
  return [...byPlace.values()];
}

/**
 * A box's colours. The rendered page is a white image in either theme, so they are fixed colours, never theme tokens:
 * `primary` is near-white in dark mode, which made the highlighted box vanish on the page.
 */
export function boxClassName(active: boolean, historical: boolean): string {
  return cn(
    "cursor-pointer outline-none transition-colors",
    historical ? "fill-transparent stroke-zinc-500/70 [stroke-dasharray:4_2]" : "fill-amber-300/15 stroke-amber-500/70",
    active && "fill-blue-500/25 stroke-blue-600",
    "hover:fill-blue-500/20 focus-visible:stroke-blue-600 focus-visible:[stroke-width:3]",
  );
}

/**
 * The field highlight overlay (docs/05 "custom components"): an SVG in the page's coordinate space laid over the page
 * image. Every provenance box of the canonical JSON is a focusable rect; the one of the hovered/focused form field
 * is emphasised, and clicking a box focuses that field. In map mode and with the table tool a selection layer on top
 * takes the pointer: clicks pick words, drags draw boxes, and the table's dividers can be moved.
 */
export function FieldHighlightOverlay({ rendered, boxes, words = [], showWords = false, highlightedPath, onHover, onSelect, labelOf = rawPath, className, interaction = null, onPick, selection = [], table = null }: FieldHighlightOverlayProps) {
  const { t } = useTranslation();
  const svgRef = useRef<SVGSVGElement>(null);
  const [drag, setDrag] = useState<{ from: { x: number; y: number }; to: { x: number; y: number } } | null>(null);
  const [divider, setDivider] = useState<{ index: number; x: number } | null>(null);
  if (rendered.width <= 0 || rendered.height <= 0) return null;

  /** A pointer position on the page in page units. */
  const pointOf = (event: { clientX: number; clientY: number }) => {
    const rect = svgRef.current?.getBoundingClientRect();
    return toNormalized({ x: event.clientX - (rect?.left ?? 0), y: event.clientY - (rect?.top ?? 0) }, rendered);
  };
  const startDrag = (event: ReactPointerEvent<SVGRectElement>) => {
    if (event.button !== 0) return;
    event.currentTarget.setPointerCapture?.(event.pointerId);
    const at = pointOf(event);
    setDrag({ from: at, to: at });
  };
  const endDrag = (event: ReactPointerEvent<SVGRectElement>) => {
    if (!drag) return;
    const box = boxFromDrag(drag.from, pointOf(event));
    setDrag(null);
    if (isClick(box)) {
      if (interaction !== "map") return;
      const index = wordAt(words, drag.from);
      if (index >= 0) onPick?.({ kind: "word", index, box: words[index]!.box, additive: event.shiftKey });
      return;
    }
    onPick?.({ kind: "region", box });
  };
  const dragRect = drag ? mapBox(boxFromDrag(drag.from, drag.to), rendered) : null;
  const tableRect = table ? mapBox(table.box, rendered) : null;
  const headerRect = table?.headerRow ? mapBox({ x0: table.box.x0, y0: table.headerRow.y0, x1: table.box.x1, y1: table.headerRow.y1 }, rendered) : null;

  return (
    <svg
      ref={svgRef}
      className={cn("absolute inset-0 overflow-visible", className)}
      width={rendered.width}
      height={rendered.height}
      viewBox={`0 0 ${rendered.width} ${rendered.height}`}
      role="group"
      aria-label={t("review.overlay.label", { count: boxes.length })}
      data-testid="field-overlay"
      data-interaction={interaction ?? undefined}
    >
      {showWords &&
        words.map((word, index) => {
          const rect = mapBox(word.box, rendered);
          return (
            <rect
              key={`w-${index}`}
              x={rect.x}
              y={rect.y}
              width={rect.width}
              height={rect.height}
              className="fill-sky-400/10 stroke-sky-500/30"
              strokeWidth={0.5}
              pointerEvents="none"
            >
              <title>{word.text}</title>
            </rect>
          );
        })}
      {shareBoxes(boxes).map((shared, index) => {
        const rect = mapBox(shared.box, rendered);
        const active = highlightedPath !== null && shared.paths.includes(highlightedPath);
        // The highlighted field when it shares the box, else the first one read there.
        const box = { path: active ? highlightedPath! : shared.paths[0]!, historical: shared.historical };
        const others = shared.paths.length - 1;
        const label = others > 0 ? t("review.overlay.shared", { field: labelOf(box.path), count: others }) : labelOf(box.path);
        return (
          <rect
            key={`${box.path}-${index}`}
            x={rect.x - 2}
            y={rect.y - 2}
            width={rect.width + 4}
            height={rect.height + 4}
            rx={2}
            tabIndex={interaction ? -1 : 0}
            role="button"
            aria-label={t("review.overlay.box", { field: label })}
            data-path={box.path}
            data-active={active}
            data-historical={box.historical}
            className={boxClassName(active, box.historical)}
            strokeWidth={active ? 2.5 : 1.5}
            onMouseEnter={() => onHover?.(box.path)}
            onMouseLeave={() => onHover?.(null)}
            onFocus={() => onHover?.(box.path)}
            onBlur={() => onHover?.(null)}
            onClick={() => onSelect?.(box.path)}
            onKeyDown={(event) => {
              if (event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                onSelect?.(box.path);
              }
            }}
          >
            <title>{label}</title>
          </rect>
        );
      })}
      {selection.map((box, index) => {
        const rect = mapBox(box, rendered);
        return <rect key={`s-${index}`} x={rect.x - 1} y={rect.y - 1} width={rect.width + 2} height={rect.height + 2} className="fill-violet-500/20 stroke-violet-600" strokeWidth={1.5} pointerEvents="none" data-testid="map-selection" />;
      })}
      {table && tableRect && (
        <g data-testid="table-drawing">
          <rect x={tableRect.x} y={tableRect.y} width={tableRect.width} height={tableRect.height} className="fill-emerald-500/5 stroke-emerald-600/70 [stroke-dasharray:6_3]" strokeWidth={1.5} pointerEvents="none" />
          {headerRect && <rect x={headerRect.x} y={headerRect.y} width={headerRect.width} height={headerRect.height} className="fill-emerald-500/15" pointerEvents="none" data-testid="table-header-band" />}
          {(table.rejected ?? []).map((box, index) => {
            const rect = mapBox(box, rendered);
            return <rect key={`r-${index}`} x={rect.x - 1} y={rect.y - 1} width={rect.width + 2} height={rect.height + 2} className="fill-red-500/15 stroke-red-600" strokeWidth={1.5} pointerEvents="none" data-testid="table-rejected-box" />;
          })}
        </g>
      )}
      {interaction && (
        <rect
          x={0}
          y={0}
          width={rendered.width}
          height={rendered.height}
          className="cursor-crosshair fill-transparent"
          data-testid="selection-layer"
          onPointerDown={startDrag}
          onPointerMove={(event) => {
            if (drag) setDrag({ from: drag.from, to: pointOf(event) });
          }}
          onPointerUp={endDrag}
          onPointerCancel={() => setDrag(null)}
          onDoubleClick={(event) => {
            if (interaction !== "table" || !table?.onDividerAdd) return;
            const at = pointOf(event);
            if (at.x > table.box.x0 && at.x < table.box.x1 && at.y > table.box.y0 && at.y < table.box.y1) table.onDividerAdd(at.x);
          }}
        />
      )}
      {table &&
        tableRect &&
        table.dividers.map((x, index) => {
          const at = divider?.index === index ? divider.x : x;
          const px = mapBox({ x0: at, y0: 0, x1: at, y1: 0 }, rendered).x;
          const movable = !!table.onDividerMove && interaction === "table";
          return (
            <g key={`d-${index}`} data-testid="table-divider" data-x={at}>
              <line x1={px} x2={px} y1={tableRect.y} y2={tableRect.y + tableRect.height} className="stroke-emerald-600" strokeWidth={divider?.index === index ? 2.5 : 1.5} pointerEvents="none" />
              {movable && (
                <rect
                  x={px - 4}
                  y={tableRect.y}
                  width={8}
                  height={tableRect.height}
                  className="cursor-col-resize fill-transparent outline-none hover:fill-emerald-500/20 focus-visible:fill-emerald-500/30"
                  role="slider"
                  aria-orientation="horizontal"
                  aria-valuemin={0}
                  aria-valuemax={1000}
                  aria-valuenow={Math.round(at)}
                  aria-label={t("review.overlay.divider", { n: index + 1 })}
                  tabIndex={0}
                  data-testid={`divider-handle-${index}`}
                  onPointerDown={(event) => {
                    event.stopPropagation();
                    event.currentTarget.setPointerCapture?.(event.pointerId);
                    setDivider({ index, x });
                  }}
                  onPointerMove={(event) => {
                    if (divider?.index === index) setDivider({ index, x: pointOf(event).x });
                  }}
                  onPointerUp={() => {
                    if (divider?.index === index) table.onDividerMove?.(index, divider.x);
                    setDivider(null);
                  }}
                  onKeyDown={(event) => {
                    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
                    event.preventDefault();
                    table.onDividerMove?.(index, x + (event.key === "ArrowLeft" ? -1 : 1) * (event.shiftKey ? 10 : 2));
                  }}
                />
              )}
            </g>
          );
        })}
      {dragRect && <rect x={dragRect.x} y={dragRect.y} width={dragRect.width} height={dragRect.height} className="fill-violet-500/10 stroke-violet-600 [stroke-dasharray:4_2]" strokeWidth={1.5} pointerEvents="none" data-testid="drag-rect" />}
    </svg>
  );
}
