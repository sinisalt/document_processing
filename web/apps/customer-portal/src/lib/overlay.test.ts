import { describe, expect, it } from "vitest";
import { collectProvenanceBoxes, mapBox, normalizedWords, toNormalized } from "./overlay";

describe("mapBox", () => {
  it("scales a normalized box onto the rendered image size", () => {
    const rect = mapBox({ x0: 100, y0: 200, x1: 300, y1: 250 }, { width: 500, height: 1000 });
    expect(rect).toEqual({ x: 50, y: 200, width: 100, height: 50 });
  });

  it("is the identity at 1000 × 1000", () => {
    expect(mapBox({ x0: 10, y0: 20, x1: 30, y1: 40 }, { width: 1000, height: 1000 })).toEqual({ x: 10, y: 20, width: 20, height: 20 });
  });

  it("maps from an arbitrary source space (a text page in PDF points)", () => {
    const rect = mapBox({ x0: 72, y0: 72, x1: 144, y1: 108 }, { width: 1224, height: 1584 }, { width: 612, height: 792 });
    expect(rect).toEqual({ x: 144, y: 144, width: 144, height: 72 });
  });

  it("tolerates inverted corners and a zero-sized rendering", () => {
    expect(mapBox({ x0: 300, y0: 250, x1: 100, y1: 200 }, { width: 500, height: 1000 })).toEqual({ x: 50, y: 200, width: 100, height: 50 });
    expect(mapBox({ x0: 0, y0: 0, x1: 500, y1: 500 }, { width: 0, height: 0 })).toEqual({ x: 0, y: 0, width: 0, height: 0 });
  });

  it("round-trips a point through toNormalized", () => {
    const rendered = { width: 800, height: 1131 };
    const rect = mapBox({ x0: 250, y0: 500, x1: 251, y1: 501 }, rendered);
    const back = toNormalized({ x: rect.x, y: rect.y }, rendered);
    expect(back.x).toBeCloseTo(250, 6);
    expect(back.y).toBeCloseTo(500, 6);
  });
});

describe("normalizedWords", () => {
  it("rescales a page stored in its own units to 1000 × 1000", () => {
    const words = normalizedWords({ number: 1, width: 500, height: 2000, words: [{ text: "Total", box: { x0: 50, y0: 100, x1: 100, y1: 120 } }] });
    expect(words[0]?.box).toEqual({ x0: 100, y0: 50, x1: 200, y1: 60 });
  });

  it("leaves an already normalized page untouched and ignores unsized pages", () => {
    const page = { number: 1, width: 1000, height: 1000, words: [{ text: "x", box: { x0: 1, y0: 2, x1: 3, y1: 4 } }] };
    expect(normalizedWords(page)).toBe(page.words);
    expect(normalizedWords({ ...page, width: 0 })).toEqual([]);
  });
});

describe("collectProvenanceBoxes", () => {
  it("finds the source box of header and line fields with their canonical paths, plus history boxes", () => {
    const canonical = {
      invoiceNumber: {
        value: { verbatim: "RE-1" },
        status: "Corrected",
        source: { method: "Human" },
        history: [{ value: { verbatim: "RE-l" }, status: "Extracted", source: { method: "Llm", page: 1, bBox: { x0: 1, y0: 2, x1: 3, y1: 4 } }, at: "", by: "llm" }],
      },
      totalGross: { value: { amount: 1, currency: "EUR" }, status: "Extracted", source: { method: "Llm", page: 2, bBox: { x0: 5, y0: 6, x1: 7, y1: 8 } } },
      notes: { value: null, status: "Missing" },
      lines: [{ lineNumber: 1, quantity: { value: 2, status: "Extracted", source: { method: "Ocr", page: 1, bBox: { x0: 9, y0: 9, x1: 10, y1: 10 } } } }],
    };
    expect(collectProvenanceBoxes(canonical)).toEqual([
      { path: "invoiceNumber", page: 1, box: { x0: 1, y0: 2, x1: 3, y1: 4 }, historical: true },
      { path: "totalGross", page: 2, box: { x0: 5, y0: 6, x1: 7, y1: 8 }, historical: false },
      { path: "lines[0].quantity", page: 1, box: { x0: 9, y0: 9, x1: 10, y1: 10 }, historical: false },
    ]);
  });

  it("skips sources without a page or box (e-invoice XML)", () => {
    expect(collectProvenanceBoxes({ currency: { value: "EUR", status: "Extracted", source: { method: "StructuredXml", rawText: "EUR" } } })).toEqual([]);
    expect(collectProvenanceBoxes(null)).toEqual([]);
  });
});
