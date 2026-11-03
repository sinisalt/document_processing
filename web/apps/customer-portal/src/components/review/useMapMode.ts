import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { ApiError, unwrap, useApi } from "@docproc/app-shell";
import { bboxParam, editableFromCandidate, grow, pickCandidate, sourceOf, union, type Box, type CorrectionSource, type FieldSpec, type MapMethod, type PageWords } from "@docproc/review";
import type { PagePick } from "./FieldHighlightOverlay";

interface Selection {
  page: number;
  /** The picked words (word clicks), in click order. */
  words: { index: number; box: Box }[];
  /** The box the API reads: the words' union, or the box drawn. */
  box: Box;
  method: MapMethod;
}

export interface MappedValue {
  path: string;
  text: string;
  source: CorrectionSource;
}

/**
 * Map mode's selection and reading (docs/11 §12.3): a pick on the page becomes a box, `GET /pages/{n}/words?bbox=`
 * reads it (the words in reading order, their text and the readings per kind), and the reading of the target field's
 * kind is the value offered; `regions:read` reads a box without words with OCR.
 */
export function useMapMode(documentId: string, target: FieldSpec | null, locale: string) {
  const { t } = useTranslation();
  const { client } = useApi();
  const [selection, setSelection] = useState<Selection | null>(null);
  const [read, setRead] = useState<PageWords | null>(null);
  const [loading, setLoading] = useState(false);
  const [ocrPending, setOcrPending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [edited, setEdited] = useState<string | null>(null);
  const request = useRef(0);

  const fetchWords = useCallback(
    async (page: number, box: Box) => {
      const id = ++request.current;
      setLoading(true);
      setError(null);
      setRead(null);
      setEdited(null);
      try {
        const result = unwrap(await client.GET("/api/v1/documents/{id}/pages/{n}/words", { params: { path: { id: documentId, n: page }, query: { bbox: bboxParam(box) } } }));
        if (id === request.current) setRead(result);
      } catch (e) {
        if (id === request.current) setError(e instanceof Error ? e.message : String(e));
      } finally {
        if (id === request.current) setLoading(false);
      }
    },
    [client, documentId],
  );

  const pick = useCallback(
    (page: number, picked: PagePick) => {
      let next: Selection;
      if (picked.kind === "word") {
        const keep = picked.additive && selection?.page === page && selection.method === "word" ? selection.words : [];
        const words = keep.some((w) => w.index === picked.index) ? keep.filter((w) => w.index !== picked.index) : [...keep, { index: picked.index, box: picked.box }];
        if (words.length === 0) {
          setSelection(null);
          setRead(null);
          return;
        }
        next = { page, words, box: grow(union(words.map((w) => w.box))!), method: "word" };
      } else {
        next = { page, words: [], box: picked.box, method: "region" };
      }
      setSelection(next);
      void fetchWords(page, next.box);
    },
    [selection, fetchWords],
  );

  const ocr = useCallback(async () => {
    if (!selection) return;
    const id = ++request.current;
    setOcrPending(true);
    setError(null);
    try {
      const result = unwrap(await client.POST("/api/v1/documents/{id}/regions:read", { params: { path: { id: documentId } }, body: { page: selection.page, bBox: selection.box } }));
      if (id === request.current) {
        setRead(result);
        setSelection({ ...selection, method: "ocr" });
      }
    } catch (e) {
      if (id !== request.current) return;
      const code = e instanceof ApiError ? (e.problem as { code?: string } | undefined)?.code : undefined;
      setError(code === "OCR_NOT_CONFIGURED" ? t("review.map.ocrNotConfigured") : e instanceof Error ? e.message : String(e));
    } finally {
      if (id === request.current) setOcrPending(false);
    }
  }, [client, documentId, selection, t]);

  const candidate = useMemo(() => (target && read ? pickCandidate(target, read.candidates) : null), [target, read]);
  const offered = useMemo(() => {
    if (!target || !read) return "";
    return candidate ? editableFromCandidate(target, candidate, locale) : (read.text ?? "");
  }, [target, read, candidate, locale]);
  const value = edited ?? offered;

  // Another target field: the same selection read as that field's kind.
  useEffect(() => setEdited(null), [target?.path]);

  const cancel = useCallback(() => {
    request.current++;
    setSelection(null);
    setRead(null);
    setLoading(false);
    setError(null);
    setEdited(null);
  }, []);

  /** The value for the target with its source, and the selection cleared; null when nothing was read. */
  const take = useCallback((): MappedValue | null => {
    if (!target || !selection || !read || read.text === null) return null;
    const mapped = { path: target.path, text: value, source: sourceOf(selection.page, read, selection.method) };
    cancel();
    return mapped;
  }, [target, selection, read, value, cancel]);

  const noWords = !loading && read !== null && read.words.length === 0 && selection?.method !== "ocr";
  return {
    selection,
    /** The boxes to draw on a page: the picked words, or the box drawn (and what it holds once read). */
    selectionBoxes: (page: number): Box[] => {
      if (!selection || selection.page !== page) return [];
      return selection.method === "word" ? selection.words.map((w) => w.box) : [selection.box];
    },
    read,
    text: read?.text ?? (noWords ? "" : null),
    loading,
    noWords,
    ocrPending,
    error,
    value,
    fits: candidate !== null,
    setValue: setEdited,
    pick,
    ocr,
    cancel,
    take,
  };
}
