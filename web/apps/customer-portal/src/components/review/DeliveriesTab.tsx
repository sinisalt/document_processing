import { useTranslation } from "react-i18next";
import { useDeliveries } from "@/components/integrations/api";
import { DeliveriesList } from "@/components/integrations/DeliveriesList";

/**
 * The document's Deliveries tab (docs/11 §19, P3): every delivery the approval or rejection fanned out, with its
 * status, destination, attempts, the receiver's answers, the external id and the payload as sent; an integration admin
 * redelivers from here. A binding whose condition was false is listed as Skipped "not applicable" (§13.10).
 */
export function DeliveriesTab({ documentId, enabled, canRedeliver }: { documentId: string; enabled: boolean; canRedeliver: boolean }) {
  const { t } = useTranslation();
  const deliveries = useDeliveries({ documentId }, enabled);
  return (
    <div className="flex flex-col gap-3" data-testid="deliveries-tab">
      <p className="text-sm text-muted-foreground">{t("review.deliveries.description")}</p>
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
