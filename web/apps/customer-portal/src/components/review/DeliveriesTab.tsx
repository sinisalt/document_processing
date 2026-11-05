import { useTranslation } from "react-i18next";
import { Unplug } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@docproc/ui";
import type { DocumentFlag } from "@/lib/queries";
import { useDeliveries } from "@/components/integrations/api";
import { DeliveriesList } from "@/components/integrations/DeliveriesList";

/**
 * The document's Deliveries tab (docs/11 §19, P3): every delivery the approval or rejection fanned out, with its
 * status, destination, attempts, the receiver's answers, the external id and the payload as sent; an integration admin
 * redelivers from here. A binding whose condition was false is listed as Skipped "not applicable" (§13.10).
 * `settledAt` is when the document was approved or rejected: the list is read again until the fan-out shows up.
 */
export function DeliveriesTab({ documentId, enabled, canRedeliver, settledAt = null, rejection = null }: { documentId: string; enabled: boolean; canRedeliver: boolean; settledAt?: string | null; rejection?: DocumentFlag | null }) {
  const { t } = useTranslation();
  const deliveries = useDeliveries({ documentId }, enabled, settledAt);
  return (
    <div className="flex flex-col gap-3" data-testid="deliveries-tab">
      <p className="text-sm text-muted-foreground">{t("review.deliveries.description")}</p>
      {/* EXTERNAL_REJECTED (docs/11 §13.8): the receiving system rejected the document after its delivery; it stays settled. */}
      {rejection && (
        <Alert variant="destructive" data-testid="deliveries-rejected">
          <Unplug aria-hidden />
          <AlertTitle>{t("review.deliveries.rejectedTitle", { destination: rejection.params.destination ?? "" })}</AlertTitle>
          <AlertDescription>{rejection.params.message ? t("review.integrationIssue.message", { message: rejection.params.message }) : t("review.integrationIssue.noMessage")}</AlertDescription>
        </Alert>
      )}
      <DeliveriesList
        deliveries={deliveries.data?.items ?? []}
        isLoading={deliveries.isPending}
        query={deliveries}
        canRedeliver={canRedeliver}
        emptyTitle={t("review.deliveries.empty")}
        emptyDescription={t("review.deliveries.emptyDescription")}
      />
    </div>
  );
}
