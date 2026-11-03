import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useNavigate } from "@tanstack/react-router";
import { Alert, AlertDescription, AlertTitle, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Skeleton, Tabs, TabsContent, TabsList, TabsTrigger } from "@docproc/ui";
import { Policies, useAuth } from "@docproc/app-shell";
import { deliveryStatuses, useDeliveries, useDestinations, useOutputsEnabled } from "./api";
import { BindingsPanel } from "./BindingsPanel";
import { DeliveriesList } from "./DeliveriesList";
import { DestinationsPanel } from "./DestinationsPanel";
import { LookupTablesPanel } from "./LookupTablesPanel";
import { MappingsPanel } from "./MappingsPanel";

export const integrationSections = ["destinations", "mappings", "bindings", "lookups", "deliveries"] as const;
export type IntegrationSection = (typeof integrationSections)[number];

export interface IntegrationsTabProps {
  section: IntegrationSection;
  onSectionChange: (section: IntegrationSection) => void;
  /** The output mapping open in the editor (a URL search parameter, so a link can point at it). */
  mappingKey: string | null;
  onMappingKeyChange: (key: string | null) => void;
}

/**
 * Settings > Integrations (docs/11 §19, P3): destinations, output mappings with the visual field map, bindings,
 * lookup tables and the delivery log. Everything needs the tenant's `outputs` switch (the admin console switches it);
 * without it the tab says so and asks nothing else of the API.
 */
export function IntegrationsTab({ section, onSectionChange, mappingKey, onMappingKeyChange }: IntegrationsTabProps) {
  const { t } = useTranslation();
  const { hasPolicy } = useAuth();
  const enabled = useOutputsEnabled();
  const canEdit = hasPolicy(Policies.IntegrationAdmin);
  const canEditLookups = hasPolicy(Policies.ConfigAdmin);

  if (enabled.isPending) return <Skeleton className="h-40 w-full" aria-busy />;
  if (!enabled.data) {
    return (
      <Alert data-testid="integrations-off">
        <AlertTitle>{t("integrations.offTitle")}</AlertTitle>
        <AlertDescription>{t("integrations.off")}</AlertDescription>
      </Alert>
    );
  }

  return (
    <Tabs value={section} onValueChange={(value) => onSectionChange(value as IntegrationSection)} data-testid="integrations">
      <TabsList aria-label={t("integrations.sectionsLabel")} className="flex-wrap">
        {integrationSections.map((value) => (
          <TabsTrigger key={value} value={value} data-testid={`integrations-${value}`}>
            {t(`integrations.sections.${value}`)}
          </TabsTrigger>
        ))}
      </TabsList>
      <TabsContent value="destinations" className="pt-4">
        {section === "destinations" && <DestinationsPanel />}
      </TabsContent>
      <TabsContent value="mappings" className="pt-4">
        {section === "mappings" && <MappingsPanel canEdit={canEdit} selectedKey={mappingKey} onSelectedKeyChange={onMappingKeyChange} />}
      </TabsContent>
      <TabsContent value="bindings" className="pt-4">
        {section === "bindings" && <BindingsPanel />}
      </TabsContent>
      <TabsContent value="lookups" className="pt-4">
        {section === "lookups" && <LookupTablesPanel canEdit={canEditLookups} />}
      </TabsContent>
      <TabsContent value="deliveries" className="pt-4">
        {section === "deliveries" && <DeliveryLog canRedeliver={canEdit} />}
      </TabsContent>
    </Tabs>
  );
}

const ALL = "__all";

/** The delivery log of every document (docs/11 §19 "Delivery log (filters, redeliver, dead letters)"). */
function DeliveryLog({ canRedeliver }: { canRedeliver: boolean }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [status, setStatus] = useState<string>(ALL);
  const [destinationId, setDestinationId] = useState<string>(ALL);
  const destinations = useDestinations();
  const deliveries = useDeliveries({ status: status === ALL ? undefined : status, destinationId: destinationId === ALL ? undefined : destinationId });
  return (
    <div className="flex flex-col gap-4" data-testid="delivery-log">
      <div className="max-w-2xl space-y-1">
        <h2 className="text-base leading-none font-semibold">{t("integrations.deliveries.title")}</h2>
        <p className="text-sm text-muted-foreground">{t("integrations.deliveries.description")}</p>
      </div>
      <div className="flex flex-wrap gap-4">
        <div className="grid gap-1">
          <Label className="text-xs">{t("integrations.deliveries.columns.status")}</Label>
          <Select value={status} onValueChange={setStatus}>
            <SelectTrigger className="w-44" data-testid="delivery-log-status">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={ALL}>{t("integrations.deliveries.allStatuses")}</SelectItem>
              {deliveryStatuses.map((value) => (
                <SelectItem key={value} value={value}>
                  {t(`integrations.deliveries.statuses.${value}`)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <div className="grid gap-1">
          <Label className="text-xs">{t("integrations.deliveries.columns.destination")}</Label>
          <Select value={destinationId} onValueChange={setDestinationId}>
            <SelectTrigger className="w-56">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={ALL}>{t("integrations.deliveries.allDestinations")}</SelectItem>
              {(destinations.data ?? []).map((d) => (
                <SelectItem key={d.id} value={d.id}>
                  {d.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
      </div>
      <DeliveriesList
        deliveries={deliveries.data?.items ?? []}
        isLoading={deliveries.isPending}
        query={deliveries}
        canRedeliver={canRedeliver}
        showDocument
        onOpenDocument={(documentId) => void navigate({ to: "/documents/$documentId", params: { documentId } })}
        emptyTitle={t("integrations.deliveries.empty")}
        emptyDescription={t("integrations.deliveries.emptyDescription")}
      />
    </div>
  );
}
