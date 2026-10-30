import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { FileQuestion } from "lucide-react";
import { Skeleton, cn } from "@docproc/ui";
import { EmptyState, queryKeys, unwrap, useApi } from "@docproc/app-shell";

/**
 * One rendered page: fetched through the typed client (the bearer token and `X-Tenant-Id` travel with the request,
 * which a plain `<img src>` cannot do) and shown from an object URL that is revoked when the thumbnail unmounts.
 */
function PageThumbnail({ documentId, page, className }: { documentId: string; page: number; className?: string }) {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const blob = useQuery({
    queryKey: [...queryKeys.documents.detail(documentId), "page", page],
    queryFn: async () => unwrap(await client.GET("/api/v1/documents/{id}/pages/{n}.png", { params: { path: { id: documentId, n: page } }, parseAs: "blob" })),
    staleTime: 5 * 60_000,
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
    <figure className={cn("flex w-32 shrink-0 flex-col gap-1", className)}>
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

/** The thumbnail strip of a document: one tile per `pageImageUrls` entry, in page order. */
export function PageThumbnails({ documentId, pageCount }: { documentId: string; pageCount: number }) {
  const { t } = useTranslation("admin");
  if (pageCount <= 0) {
    return <EmptyState title={t("document.noPages")} description={t("document.noPagesDescription")} icon={FileQuestion} />;
  }
  return (
    <ul className="flex gap-3 overflow-x-auto pb-2" aria-label={t("document.pages")}>
      {Array.from({ length: pageCount }, (_, i) => (
        <li key={i}>
          <PageThumbnail documentId={documentId} page={i + 1} />
        </li>
      ))}
    </ul>
  );
}
