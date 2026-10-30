import { isFieldValue, type BoundingBox } from "@docproc/app-shell";

/** The page's coordinate space as the text layer and provenance boxes use it (docs/04): 1000 × 1000 units. */
export const NORMALIZED_SIZE = 1000;

export interface Size {
  width: number;
  height: number;
}

/** A box in CSS pixels of the rendered page image. */
export interface PixelRect {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * Maps a box from the normalized space (or any source space, when `source` is given) onto the rendered image. Boxes
 * are axis-aligned, so only the two scale factors matter; a zero-sized rendering yields a zero rect rather than NaN.
 */
export function mapBox(box: BoundingBox, rendered: Size, source: Size = { width: NORMALIZED_SIZE, height: NORMALIZED_SIZE }): PixelRect {
  const sx = source.width > 0 ? rendered.width / source.width : 0;
  const sy = source.height > 0 ? rendered.height / source.height : 0;
  const x0 = Math.min(box.x0, box.x1);
  const y0 = Math.min(box.y0, box.y1);
  return {
    x: x0 * sx,
    y: y0 * sy,
    width: Math.abs(box.x1 - box.x0) * sx,
    height: Math.abs(box.y1 - box.y0) * sy,
  };
}

/** The inverse: a point on the rendered image (CSS pixels) back into the normalized space. */
export function toNormalized(point: { x: number; y: number }, rendered: Size): { x: number; y: number } {
  return {
    x: rendered.width > 0 ? (point.x / rendered.width) * NORMALIZED_SIZE : 0,
    y: rendered.height > 0 ? (point.y / rendered.height) * NORMALIZED_SIZE : 0,
  };
}

/** `GET /documents/{id}/text` (`src/DocProc.Domain/Text/TextLayer.cs` through `PipelineJson`). */
export interface TextLayerWord {
  text: string;
  box: BoundingBox;
  confidence?: number | null;
}

export interface TextLayerPage {
  number: number;
  width: number;
  height: number;
  words: TextLayerWord[];
}

export interface TextLayer {
  source: "PdfText" | "Ocr" | string;
  pages: TextLayerPage[];
}

/**
 * Word boxes of one page in the normalized space. A stored page may still be in its own units (PDF points, OCR
 * pixels); `TextPage.Normalize` on the server does the same rescale, so a page that is already 1000 × 1000 is a no-op.
 */
export function normalizedWords(page: TextLayerPage): TextLayerWord[] {
  if (page.width <= 0 || page.height <= 0) return [];
  const sx = NORMALIZED_SIZE / page.width;
  const sy = NORMALIZED_SIZE / page.height;
  if (sx === 1 && sy === 1) return page.words;
  return page.words.map((word) => ({
    ...word,
    box: { x0: word.box.x0 * sx, y0: word.box.y0 * sy, x1: word.box.x1 * sx, y1: word.box.y1 * sy },
  }));
}

/** One provenance box: where a field (or a value it carried before a correction) was read on the page. */
export interface ProvenanceBox {
  path: string;
  page: number;
  box: BoundingBox;
  /** False for the box of the current source, true for a box out of the field's history. */
  historical: boolean;
}

function isBox(value: unknown): value is BoundingBox {
  if (typeof value !== "object" || value === null) return false;
  const b = value as Record<string, unknown>;
  return ["x0", "y0", "x1", "y1"].every((key) => typeof b[key] === "number");
}

/**
 * Walks the canonical JSON and collects every `FieldValue.source.bBox` (and the boxes in each field's `history`)
 * with its canonical path, so the overlay can draw them and the form can find the box of a field.
 */
export function collectProvenanceBoxes(canonical: unknown): ProvenanceBox[] {
  const boxes: ProvenanceBox[] = [];
  const visit = (node: unknown, path: string) => {
    if (node === null || typeof node !== "object") return;
    if (isFieldValue(node)) {
      const source = node.source;
      if (source && typeof source.page === "number" && isBox(source.bBox)) {
        boxes.push({ path, page: source.page, box: source.bBox, historical: false });
      }
      for (const entry of node.history ?? []) {
        const hs = entry.source;
        if (hs && typeof hs.page === "number" && isBox(hs.bBox)) {
          boxes.push({ path, page: hs.page, box: hs.bBox, historical: true });
        }
      }
      return;
    }
    if (Array.isArray(node)) {
      node.forEach((item, index) => visit(item, `${path}[${index}]`));
      return;
    }
    for (const [key, value] of Object.entries(node as Record<string, unknown>)) {
      visit(value, path === "" ? key : `${path}.${key}`);
    }
  };
  visit(canonical, "");
  return boxes;
}
