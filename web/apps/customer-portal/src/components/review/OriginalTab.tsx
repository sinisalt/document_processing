import { useTranslation } from "react-i18next";
import { useMutation } from "@tanstack/react-query";
import { Download, Loader2 } from "lucide-react";
import { Button } from "@docproc/ui";
import { formatDateTime, unwrap, useApi } from "@docproc/app-shell";
import type { DocumentDetail } from "@/lib/queries";

/**
 * The Original tab: where the file came from and a download of `GET /documents/{id}/original`. The download goes
 * through the authenticated client (a plain link cannot carry the bearer token) and is handed to the browser as a blob.
 */
export function OriginalTab({ document }: { document: DocumentDetail }) {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const fileName = document.ingestion?.fileName ?? `${document.documentNumber ?? document.id}.bin`;

  const download = useMutation({
    mutationFn: async () => unwrap(await client.GET("/api/v1/documents/{id}/original", { params: { path: { id: document.id } }, parseAs: "blob" })) as Blob,
    onSuccess: (blob) => {
      const url = URL.createObjectURL(blob);
      const anchor = window.document.createElement("a");
      anchor.href = url;
      anchor.download = fileName;
      anchor.rel = "noopener";
      window.document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
      setTimeout(() => URL.revokeObjectURL(url), 10_000);
    },
  });

  return (
    <div className="flex flex-col gap-4" data-testid="original-tab">
      <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
        <dt className="text-muted-foreground">{t("review.original.fileName")}</dt>
        <dd>{document.ingestion?.fileName ?? "—"}</dd>
        <dt className="text-muted-foreground">{t("review.original.receivedAt")}</dt>
        <dd>{document.ingestion ? formatDateTime(document.ingestion.receivedAt, i18n.language) : "—"}</dd>
        <dt className="text-muted-foreground">{t("review.original.source")}</dt>
        <dd>{document.ingestion?.sourceFrom ?? "—"}</dd>
        <dt className="text-muted-foreground">{t("review.original.pages")}</dt>
        <dd>{String(document.pageCount)}</dd>
        <dt className="text-muted-foreground">{t("review.original.extraction")}</dt>
        <dd>{document.extractionMethod ? t(`common:sourceMethod.${document.extractionMethod}`, { defaultValue: document.extractionMethod }) : "—"}</dd>
      </dl>
      <div>
        <Button type="button" variant="outline" onClick={() => download.mutate()} disabled={download.isPending} aria-busy={download.isPending}>
          {download.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <Download aria-hidden />}
          {t("review.original.download")}
        </Button>
      </div>
    </div>
  );
}
