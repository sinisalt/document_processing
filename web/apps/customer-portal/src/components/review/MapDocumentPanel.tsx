import { useState } from "react";
import { useTranslation } from "react-i18next";
import { FileQuestion } from "lucide-react";
import { Separator } from "@docproc/ui";
import type { DocumentDetail } from "@/lib/queries";
import { CreateSupplierDialog, SetTypeForm, SupplierPicker } from "./ReviewDialogs";

/**
 * The "Map this document" panel (docs/11 §12.1 step 2) in place of the empty form of a document the system did not
 * recognise, or whose type has no form yet: pick its type and how to start (empty, to fill from the page in map mode,
 * or pre-filled by AI), and its supplier (or create one from the document).
 */
export function MapDocumentPanel({ document, locale, hasForm = false }: { document: DocumentDetail; locale: string; hasForm?: boolean }) {
  const { t } = useTranslation();
  const [creating, setCreating] = useState(false);
  const unknown = document.type === "Unknown";
  const typeName = document.typeName ?? t(`documentType.${document.type}`, { defaultValue: document.type });
  return (
    <section className="flex flex-col gap-4" aria-labelledby="map-document-heading" data-testid="map-document-panel">
      <div className="flex items-start gap-3">
        <FileQuestion className="mt-0.5 size-5 shrink-0 text-muted-foreground" aria-hidden />
        <div className="grid gap-1">
          <h2 id="map-document-heading" className="text-base font-semibold">
            {t("review.mapDocument.title")}
          </h2>
          <p className="text-sm text-muted-foreground">
            {unknown ? t("review.mapDocument.unknown") : hasForm ? t("review.mapDocument.noData", { type: typeName }) : t("review.mapDocument.noForm", { type: typeName })}
          </p>
        </div>
      </div>
      <SetTypeForm document={document} locale={locale} />
      <Separator />
      <div className="grid gap-2">
        <h3 className="text-sm font-medium">{t("review.mapDocument.supplier")}</h3>
        <SupplierPicker document={document} onCreate={() => setCreating(true)} />
      </div>
      {creating && <CreateSupplierDialog document={document} open onOpenChange={setCreating} />}
    </section>
  );
}
