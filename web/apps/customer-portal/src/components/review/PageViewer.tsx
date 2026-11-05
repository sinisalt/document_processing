import { useEffect, useLayoutEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useQuery } from "@tanstack/react-query";
import { ChevronLeft, ChevronRight, FileText, Fingerprint, MousePointerClick, Table2, Type, ZoomIn, ZoomOut } from "lucide-react";
import { Button, Skeleton, Toggle } from "@docproc/ui";
import { EmptyState, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import type { Box } from "@docproc/review";
import { FieldHighlightOverlay, type PagePick, type TableDrawing } from "./FieldHighlightOverlay";
import { normalizedWords, type ProvenanceBox, type Size, type TextLayer } from "@/lib/overlay";

const ZOOM_STEPS = [0.5, 0.75, 1, 1.25, 1.5, 2, 3];

/** Map mode and the table tool in the viewer's toolbar (docs/11 §12.3, §12.4); absent when the document cannot be mapped. */
export interface PageViewerTools {
  mapMode: boolean;
  /** Absent when only Identify mode is offered (a tenant without configurableTypes). */
  onMapModeChange?: (on: boolean) => void;
  /** The table tool is offered only for a type with a repeating group. */
  tableMode?: boolean;
  onTableModeChange?: (on: boolean) => void;
  onPick: (page: number, pick: PagePick) => void;
  /** The map selection on the page shown. */
  selection?: readonly Box[];
  /** The table tool's drawing on the page shown. */
  table?: TableDrawing | null;
  /** Shown between the toolbar and the page: map mode's preview. */
  banner?: ReactNode;
  /**
   * Identify mode (docs/11 §12.9, shortcut I): the same selection as map mode, but a pick becomes an identifier of the
   * layout instead of a field value. Offered only where the document can be saved as a layout.
   */
  identifyMode?: boolean;
  onIdentifyModeChange?: (on: boolean) => void;
  /** The identification evidence on the page shown, drawn while Identify mode is on. */
  evidence?: readonly { box: Box; label: string; positive: boolean }[];
}

export interface PageViewerProps {
  documentId: string;
  pageCount: number;
  boxes: ProvenanceBox[];
  highlightedPath: string | null;
  onHover: (path: string | null) => void;
  onSelect: (path: string) => void;
  page: number;
  onPageChange: (page: number) => void;
  /** The field's label for the overlay boxes (their tooltip and accessible name). */
  fieldLabel?: (path: string) => string;
  tools?: PageViewerTools;
}

/** Fetches a binary through the authenticated client and keeps an object URL for it while mounted. */
function useObjectUrl(blob: Blob | undefined): string | null {
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    if (!blob) {
      setUrl(null);
      return;
    }
    const created = URL.createObjectURL(blob);
    setUrl(created);
    return () => URL.revokeObjectURL(created);
  }, [blob]);
  return url;
}

/**
 * The left half of the review screen: the rendered page (`GET /documents/{id}/pages/{n}.png`, fetched with the bearer
 * token, so an `<img src>` cannot be used directly), zoom, page navigation and the field highlight overlay on top.
 */
export function PageViewer({ documentId, pageCount, boxes, highlightedPath, onHover, onSelect, page, onPageChange, fieldLabel, tools }: PageViewerProps) {
  const { t } = useTranslation();
  const { client } = useApi();
  const [zoomIndex, setZoomIndex] = useState(2);
  const [showWords, setShowWords] = useState(false);
  const [rendered, setRendered] = useState<Size>({ width: 0, height: 0 });
  const imageRef = useRef<HTMLImageElement>(null);
  const zoom = ZOOM_STEPS[zoomIndex] ?? 1;

  const image = useQuery({
    // Outside the `documents` prefix (docs/05): saving or deciding invalidates `documents.all`, and a rendered page
    // never changes, so it must not be downloaded again.
    queryKey: queryKeys.documentPages.page(documentId, page),
    queryFn: async () => unwrap(await client.GET("/api/v1/documents/{id}/pages/{n}.png", { params: { path: { id: documentId, n: page } }, parseAs: "blob" })),
    enabled: pageCount > 0 && page >= 1 && page <= pageCount,
    staleTime: Infinity,
  });
  const imageUrl = useObjectUrl(image.data as Blob | undefined);

  const text = useQuery({
    queryKey: queryKeys.documents.text(documentId),
    queryFn: async () => unwrap(await client.GET("/api/v1/documents/{id}/text", { params: { path: { id: documentId } } })) as unknown as TextLayer,
    enabled: pageCount > 0,
    staleTime: Infinity,
    meta: { silent: true },
    retry: false,
  });

  const words = useMemo(() => {
    const textPage = text.data?.pages?.find((p) => p.number === page);
    return textPage ? normalizedWords(textPage) : [];
  }, [text.data, page]);

  const pageBoxes = useMemo(() => boxes.filter((box) => box.page === page), [boxes, page]);

  // The overlay follows the image's rendered size (zoom, container width, window size).
  useLayoutEffect(() => {
    const element = imageRef.current;
    if (!element || typeof ResizeObserver === "undefined") return;
    const update = () => setRendered({ width: element.clientWidth, height: element.clientHeight });
    update();
    const observer = new ResizeObserver(update);
    observer.observe(element);
    return () => observer.disconnect();
  }, [imageUrl]);

  if (pageCount <= 0) {
    return (
      <div className="p-4">
        <EmptyState icon={FileText} title={t("review.viewer.noPagesTitle")} description={t("review.viewer.noPages")} />
      </div>
    );
  }

  return (
    <div className="flex h-full min-h-0 flex-col" data-testid="page-viewer">
      <div className="flex flex-wrap items-center gap-1 border-b px-2 py-1" role="toolbar" aria-label={t("review.viewer.toolbar")}>
        <Button variant="ghost" size="icon-sm" aria-label={t("review.viewer.previousPage")} onClick={() => onPageChange(page - 1)} disabled={page <= 1}>
          <ChevronLeft aria-hidden />
        </Button>
        <span className="text-sm tabular-nums" aria-live="polite">
          {t("review.viewer.pageOf", { page, count: pageCount })}
        </span>
        <Button variant="ghost" size="icon-sm" aria-label={t("review.viewer.nextPage")} onClick={() => onPageChange(page + 1)} disabled={page >= pageCount}>
          <ChevronRight aria-hidden />
        </Button>
        <span className="mx-1 h-4 w-px bg-border sm:mx-2" aria-hidden />
        <Button variant="ghost" size="icon-sm" aria-label={t("review.viewer.zoomOut")} onClick={() => setZoomIndex((i) => Math.max(0, i - 1))} disabled={zoomIndex === 0}>
          <ZoomOut aria-hidden />
        </Button>
        <span className="w-12 text-center text-sm tabular-nums">{Math.round(zoom * 100)}%</span>
        <Button
          variant="ghost"
          size="icon-sm"
          aria-label={t("review.viewer.zoomIn")}
          onClick={() => setZoomIndex((i) => Math.min(ZOOM_STEPS.length - 1, i + 1))}
          disabled={zoomIndex === ZOOM_STEPS.length - 1}
        >
          <ZoomIn aria-hidden />
        </Button>
        <span className="mx-1 h-4 w-px bg-border sm:mx-2" aria-hidden />
        <Toggle pressed={showWords} onPressedChange={setShowWords} size="sm" aria-label={t("review.viewer.showWords")} disabled={!text.data}>
          <Type aria-hidden />
          <span className="hidden lg:inline">{t("review.viewer.words")}</span>
        </Toggle>
        {tools && (
          <>
            {tools.onMapModeChange && (
              <Toggle
                pressed={tools.mapMode}
                onPressedChange={tools.onMapModeChange}
                size="sm"
                aria-label={t("review.viewer.mapMode")}
                aria-keyshortcuts="M"
                title={t("review.viewer.mapModeHint")}
                data-testid="map-mode-toggle"
              >
                <MousePointerClick aria-hidden />
                <span className="hidden lg:inline">{t("review.viewer.map")}</span>
              </Toggle>
            )}
            {tools.onIdentifyModeChange && (
              <Toggle
                pressed={!!tools.identifyMode}
                onPressedChange={tools.onIdentifyModeChange}
                size="sm"
                aria-label={t("review.viewer.identifyMode")}
                aria-keyshortcuts="I"
                title={t("review.viewer.identifyModeHint")}
                data-testid="identify-mode-toggle"
              >
                <Fingerprint aria-hidden />
                <span className="hidden lg:inline">{t("review.viewer.identify")}</span>
              </Toggle>
            )}
            {tools.onTableModeChange && (
              <Toggle pressed={!!tools.tableMode} onPressedChange={tools.onTableModeChange} size="sm" aria-label={t("review.viewer.tableTool")} title={t("review.viewer.tableToolHint")} data-testid="table-tool-toggle">
                <Table2 aria-hidden />
                <span className="hidden lg:inline">{t("review.viewer.table")}</span>
              </Toggle>
            )}
          </>
        )}
      </div>
      {tools?.banner && (
        <div className="border-b bg-background px-3 py-2" data-testid="viewer-banner">
          {tools.banner}
        </div>
      )}
      <div className="relative min-h-0 flex-1 overflow-auto bg-muted/40 p-4">
        {image.isPending && <Skeleton className="mx-auto aspect-[1/1.414] w-full max-w-3xl" />}
        {image.isError && <EmptyState title={t("review.viewer.pageError")} className="mx-auto max-w-md" />}
        {imageUrl && (
          <div className="relative mx-auto w-fit" style={{ width: `${zoom * 100}%`, maxWidth: zoom <= 1 ? "48rem" : undefined }}>
            <img ref={imageRef} src={imageUrl} alt={t("review.viewer.pageAlt", { page })} className="block h-auto w-full select-none shadow-sm" draggable={false} />
            <FieldHighlightOverlay
              rendered={rendered}
              boxes={pageBoxes}
              words={words}
              showWords={showWords || !!tools?.mapMode || !!tools?.identifyMode}
              highlightedPath={highlightedPath}
              onHover={onHover}
              onSelect={onSelect}
              labelOf={fieldLabel}
              interaction={tools?.tableMode ? "table" : tools?.identifyMode ? "identify" : tools?.mapMode ? "map" : null}
              onPick={tools ? (pick) => tools.onPick(page, pick) : undefined}
              selection={tools?.selection}
              table={tools?.table}
              evidence={tools?.identifyMode ? tools.evidence : undefined}
            />
          </div>
        )}
      </div>
    </div>
  );
}
