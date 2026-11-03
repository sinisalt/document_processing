import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { queryKeys, unwrap, useApi } from "@docproc/app-shell";
import {
  addDivider,
  dividers,
  moveDivider,
  pageReport,
  removeDivider,
  rowFieldsOf,
  tableRequest,
  toBox,
  withTarget,
  type Box,
  type ColumnTarget,
  type FormSpec,
  type TableMargins,
  type TableReport,
  type TableToolState,
} from "@docproc/review";
import { useTableReports, type DocumentDetail } from "@/lib/queries";
import { useSessionState } from "@/lib/reviewSession";
import type { TableDrawing } from "./FieldHighlightOverlay";

export type TableReportKind = "proposal" | "preview" | "stale" | "stored";

/**
 * The table tool's state and calls (docs/11 §12.4): a box drawn on a page asks `regions:table` for the columns; the
 * reviewer names their fields and moves the dividers; Preview reads every page (`pages: "1-*"`); Write stores the rows
 * as one correction and the report, and the stored report (`GET /table-reports`) is the page strip from then on.
 */
export function useTableTool(document: DocumentDetail, formSpec: FormSpec | null, groupPath: string | null, enabled: boolean) {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [state, setState] = useSessionState(document.id, "table", null);
  const [report, setReport] = useState<TableReport | null>(null);
  const [kind, setKind] = useState<TableReportKind | null>(null);
  const [error, setError] = useState<string | null>(null);
  const stored = useTableReports(document.id, enabled && groupPath !== null);
  const storedReport = useMemo(() => stored.data?.find((r) => r.path === (state?.path ?? groupPath))?.report ?? null, [stored.data, state?.path, groupPath]);
  const fields = useMemo(() => rowFieldsOf(formSpec, state?.path ?? groupPath ?? ""), [formSpec, state?.path, groupPath]);

  const shown = report ?? storedReport;
  const shownKind: TableReportKind | null = report ? kind : storedReport ? "stored" : null;

  const call = useCallback(
    async (body: ReturnType<typeof tableRequest> | { page: number; bBox: Box; path?: string | null; groupHeader?: { target: string } | null }) =>
      unwrap(await client.POST("/api/v1/documents/{id}/regions:table", { params: { path: { id: document.id } }, body: body as ReturnType<typeof tableRequest> })),
    [client, document.id],
  );

  // The proposal reads its page with the group headings the panel starts with ("Group headings fill the category" is
  // ticked when the row has a category), so a heading is not read as an article there either.
  const propose = useMutation({
    mutationFn: async ({ page, box }: { page: number; box: Box }) =>
      call({ page, bBox: box, path: groupPath, groupHeader: rowFieldsOf(formSpec, groupPath ?? "").hasCategory ? { target: "category" } : null }),
    onMutate: () => setError(null),
    onSuccess: (answer, { page, box }) => {
      const hasCategory = rowFieldsOf(formSpec, answer.path).hasCategory;
      setState({
        page,
        box,
        columns: answer.columns,
        headerRow: answer.headerRow,
        rowAnchor: answer.rowAnchor,
        path: answer.path,
        allPages: true,
        groupHeadingTarget: hasCategory ? "category" : null,
        skipLines: [],
        stopLines: [],
        margins: { top: null, bottom: null },
        locale: answer.locale ?? null,
      });
      setReport(answer.report);
      setKind("proposal");
    },
    onError: (e) => setError(e instanceof Error ? e.message : String(e)),
    meta: { silent: true },
  });

  const preview = useMutation({
    mutationFn: async (current: TableToolState) => call(tableRequest(current, false)),
    onMutate: () => setError(null),
    onSuccess: (answer) => {
      setReport(answer.report);
      setKind("preview");
    },
    onError: (e) => setError(e instanceof Error ? e.message : String(e)),
    meta: { silent: true },
  });

  const write = useMutation({
    mutationFn: async (current: TableToolState) => call(tableRequest(current, true, t("review.table.reason"))),
    onMutate: () => setError(null),
    onSuccess: (answer) => {
      setReport(null);
      setKind(null);
      setState(null);
      if (answer.document) queryClient.setQueryData(queryKeys.documents.detail(document.id), answer.document);
      void queryClient.invalidateQueries({ queryKey: queryKeys.documents.all });
      toast.success(t("review.table.written", { count: Number(answer.report.rows) }));
    },
    onError: (e) => setError(e instanceof Error ? e.message : String(e)),
    meta: { silent: true },
  });

  /** Any change to the mapping makes a preview stale: Write needs a fresh one. */
  const change = useCallback(
    (next: TableToolState) => {
      setState(next);
      if (kind === "preview") setKind("stale");
    },
    [kind, setState],
  );

  const drawing = useCallback(
    (page: number): TableDrawing | null => {
      if (state && page === state.page) {
        return {
          box: state.box,
          dividers: dividers(state.columns),
          headerRow: state.headerRow ? { y0: Number(state.headerRow.y0), y1: Number(state.headerRow.y1) } : null,
          rejected: (pageReport(shown, page)?.rejectedLines ?? []).map((l) => toBox(l.bBox)),
          onDividerMove: (index, x) => change({ ...state, columns: moveDivider(state.columns, index, x) }),
          onDividerAdd: (x) => change({ ...state, columns: addDivider(state.columns, x) }),
        };
      }
      // Another page of the report: the dividers used there (measured again at its header, or carried over) and the band
      // of the page read (below its header row, within the margins), read only. A report stored before it recorded the
      // band draws the box's height.
      const here = pageReport(shown, page);
      if (!here || here.columns.length === 0) return null;
      const cols = here.columns.map((c) => ({ x0: Number(c.x0), x1: Number(c.x1) }));
      const top = here.top !== null && here.top !== undefined ? Number(here.top) : page === state?.page ? (state?.box.y0 ?? 0) : 0;
      const bottom = here.bottom !== null && here.bottom !== undefined ? Number(here.bottom) : (state?.box.y1 ?? 1000);
      return {
        box: { x0: cols[0]!.x0, y0: top, x1: cols[cols.length - 1]!.x1, y1: bottom },
        dividers: dividers(cols),
        rejected: here.rejectedLines.map((l) => toBox(l.bBox)),
      };
    },
    [state, shown, change],
  );

  return {
    state,
    report: shown,
    reportKind: shownKind,
    error,
    rowFields: fields.fields,
    hasTiers: fields.hasTiers,
    hasCategory: fields.hasCategory,
    proposing: propose.isPending,
    previewing: preview.isPending,
    writing: write.isPending,
    drawing,
    /** A box drawn on a page: the start of a mapping (a box drawn while one exists is ignored; Start over first). */
    onRegion: (page: number, box: Box) => {
      if (state || propose.isPending) return;
      propose.mutate({ page, box });
    },
    onColumnTarget: (index: number, target: ColumnTarget) => state && change({ ...state, columns: state.columns.map((c, i) => (i === index ? withTarget(c, target) : c)) }),
    onRemoveDivider: (index: number) => state && change({ ...state, columns: removeDivider(state.columns, index) }),
    onAllPagesChange: (allPages: boolean) => state && change({ ...state, allPages }),
    onGroupHeadingsChange: (on: boolean) => state && change({ ...state, groupHeadingTarget: on ? "category" : null }),
    onSkipLinesChange: (skipLines: string[]) => state && change({ ...state, skipLines }),
    onStopLinesChange: (stopLines: string[]) => state && change({ ...state, stopLines }),
    onMarginsChange: (margins: TableMargins) => state && change({ ...state, margins }),
    onPreview: () => state && preview.mutate(state),
    onWrite: () => state && write.mutate(state),
    onReset: () => {
      setState(null);
      setReport(null);
      setKind(null);
      setError(null);
    },
  };
}
