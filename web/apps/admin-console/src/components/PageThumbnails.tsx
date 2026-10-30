import { useEffect, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { FileQuestion } from "lucide-react";
import { Button, Skeleton, cn } from "@docproc/ui";
import { ApiError, EmptyState, queryKeys, unwrap, useApi } from "@docproc/app-shell";

/** Tiles rendered before "Show all": a 200-page scan must not fire 200 image requests on open. */
export const INITIAL_THUMBNAILS = 12;

/** A page that is not rendered (yet) answers 404; asking again will not change that. Other failures retry twice. */
export function retryPageImage(failureCount: number, error: unknown): boolean {
  if (error instanceof ApiError && error.status === 404) return false;
  return failureCount < 2;
}

/** True once the element has come near the viewport (and stays true); immediately where IntersectionObserver is missing. */
function useNearViewport<T extends Element>(): [React.RefObject<T | null>, boolean] {
  const ref = useRef<T | null>(null);
  const [near, setNear] = useState(() => typeof IntersectionObserver === "undefined");
  useEffect(() => {
    if (near || !ref.current) return;
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) {
        setNear(true);
        observer.disconnect();
      }
    }, { rootMargin: "200px" });
    observer.observe(ref.current);
    return () => observer.disconnect();
  }, [near]);
  return [ref, near];
}

/**
 * One rendered page: fetched through the typed client (the bearer token and `X-Tenant-Id` travel with the request,
 * which a plain `<img src>` cannot do) once the tile scrolls near the viewport, and shown from an object URL that is
 * revoked when the thumbnail unmounts. Keyed under `document-pages`, not `documents`, so a document action's
 * invalidation does not refetch every image.
 */
function PageThumbnail({ documentId, page, className }: { documentId: string; page: number; className?: string }) {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const [ref, near] = useNearViewport<HTMLElement>();
  const blob = useQuery({
    queryKey: queryKeys.documentPages.page(documentId, page),
    queryFn: async () => unwrap(await client.GET("/api/v1/documents/{id}/pages/{n}.png", { params: { path: { id: documentId, n: page } }, parseAs: "blob" })),
    enabled: near,
    staleTime: Infinity,
    retry: retryPageImage,
    meta: { silent: true },
  });
  const [url, setUrl] = useState<string | null>(null);
  useEffect(() => {
    if (!blob.data) return;
    const next = URL.createObjectURL(blob.data);
    setUrl(next);
    return () => URL.revokeObjectURL(next);
  }, [blob.data]);

  return (
    <figure ref={ref} className={cn("flex w-32 shrink-0 flex-col gap-1", className)} data-page={page}>
      <div className="flex aspect-[1/1.4] items-center justify-center overflow-hidden rounded-md border bg-muted">
        {blob.isPending ? (
          <Skeleton className="size-full" />
        ) : blob.isError || !url ? (
          <FileQuestion className="size-6 text-muted-foreground" aria-label={t("document.pageUnavailable")} />
        ) : (
          <img src={url} alt={t("document.pageAlt", { page })} className="size-full object-contain" />
        )}
      </div>
      <figcaption className="text-center text-xs text-muted-foreground">{t("document.pageLabel", { page })}</figcaption>
    </figure>
  );
}

/** The thumbnail strip of a document: the first {@link INITIAL_THUMBNAILS} pages, the rest behind "Show all". */
export function PageThumbnails({ documentId, pageCount }: { documentId: string; pageCount: number }) {
  const { t } = useTranslation("admin");
  const [showAll, setShowAll] = useState(false);
  if (pageCount <= 0) {
    return <EmptyState title={t("document.noPages")} description={t("document.noPagesDescription")} icon={FileQuestion} />;
  }
  const shown = showAll ? pageCount : Math.min(pageCount, INITIAL_THUMBNAILS);
  return (
    <div className="flex flex-col gap-2">
      <ul className="flex gap-3 overflow-x-auto pb-2" aria-label={t("document.pages")}>
        {Array.from({ length: shown }, (_, i) => (
          <li key={i}>
            <PageThumbnail documentId={documentId} page={i + 1} />
          </li>
        ))}
      </ul>
      {shown < pageCount && (
        <div className="flex items-center gap-2 text-sm text-muted-foreground">
          <span>{t("document.pagesShowing", { shown, count: pageCount })}</span>
          <Button variant="link" size="sm" className="h-auto p-0" onClick={() => setShowAll(true)}>
            {t("document.pagesShowAll", { count: pageCount })}
          </Button>
        </div>
      )}
    </div>
  );
}
