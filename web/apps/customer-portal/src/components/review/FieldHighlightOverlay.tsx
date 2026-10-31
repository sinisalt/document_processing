import { useTranslation } from "react-i18next";
import { cn } from "@docproc/ui";
import { mapBox, type ProvenanceBox, type Size, type TextLayerWord } from "@/lib/overlay";

export interface FieldHighlightOverlayProps {
  /** The rendered size of the page image in CSS pixels; the SVG covers exactly that area. */
  rendered: Size;
  /** Provenance boxes of this page (already filtered by page). */
  boxes: ProvenanceBox[];
  /** Word boxes of this page in the normalized space; drawn faintly when `showWords`. */
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
}

const rawPath = (path: string) => path;

/**
 * The field highlight overlay (docs/05 "custom components"): an SVG in the page's coordinate space laid over the page
 * image. Every provenance box of the canonical JSON is a focusable rect; the one of the hovered/focused form field
 * is emphasised, and clicking a box focuses that field.
 */
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

export function FieldHighlightOverlay({ rendered, boxes, words = [], showWords = false, highlightedPath, onHover, onSelect, labelOf = rawPath, className }: FieldHighlightOverlayProps) {
  const { t } = useTranslation();
  if (rendered.width <= 0 || rendered.height <= 0) return null;
  return (
    <svg
      className={cn("absolute inset-0 overflow-visible", className)}
      width={rendered.width}
      height={rendered.height}
      viewBox={`0 0 ${rendered.width} ${rendered.height}`}
      role="group"
      aria-label={t("review.overlay.label", { count: boxes.length })}
      data-testid="field-overlay"
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
      {boxes.map((box, index) => {
        const rect = mapBox(box.box, rendered);
        const active = highlightedPath !== null && box.path === highlightedPath;
        const label = labelOf(box.path);
        return (
          <rect
            key={`${box.path}-${index}`}
            x={rect.x - 2}
            y={rect.y - 2}
            width={rect.width + 4}
            height={rect.height + 4}
            rx={2}
            tabIndex={0}
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
    </svg>
  );
}
