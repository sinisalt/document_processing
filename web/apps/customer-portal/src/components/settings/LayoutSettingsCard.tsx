import { useEffect, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2 } from "lucide-react";
import { Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Input, Label, Switch, Textarea } from "@docproc/ui";
import { ApiError, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import type { TenantSettings } from "@/lib/queries";
import { splitLines } from "@/lib/routingQueries";

interface Draft {
  layoutReviewWarmup: string;
  layoutAgreementPercent: string;
  layoutsRequiredByDefault: boolean;
  trustedForwarderDomains: string;
  supplierAttributeSchema: string;
}

function toDraft(data: TenantSettings | undefined): Draft {
  const s = data?.settings;
  const schema = s?.supplierAttributeSchema as unknown;
  return {
    layoutReviewWarmup: String(s?.layoutReviewWarmup ?? 5),
    layoutAgreementPercent: String(s?.layoutAgreementPercent ?? 90),
    layoutsRequiredByDefault: s?.layoutsRequiredByDefault === true,
    trustedForwarderDomains: (s?.trustedForwarderDomains ?? []).join("\n"),
    supplierAttributeSchema: schema && typeof schema === "object" && Object.keys(schema).length > 0 ? JSON.stringify(schema, null, 2) : "",
  };
}

/**
 * The settings body with the layout and routing settings (docs/05 "Settings"): the members of the general policy are sent
 * as stored (PUT replaces them), the warm-up and agreement only with supplierLayouts, the routing ones only with
 * sourceRouting (sending back what GET gave needs no switch).
 */
export function layoutSettingsBody(data: TenantSettings, draft: Draft, features: { supplierLayouts: boolean; sourceRouting: boolean }) {
  const s = data.settings;
  const body: Record<string, unknown> = {
    autoApproveConfidenceThreshold: s.autoApproveConfidenceThreshold,
    alwaysReviewSupplierIds: s.alwaysReviewSupplierIds,
    retentionYears: s.retentionYears,
    shareExtractorProfiles: s.shareExtractorProfiles,
  };
  if (features.supplierLayouts) {
    body.layoutReviewWarmup = Number(draft.layoutReviewWarmup);
    body.layoutAgreementPercent = Number(draft.layoutAgreementPercent);
  }
  if (features.sourceRouting) {
    body.layoutsRequiredByDefault = draft.layoutsRequiredByDefault;
    body.trustedForwarderDomains = splitLines(draft.trustedForwarderDomains);
    body.supplierAttributeSchema = draft.supplierAttributeSchema.trim() === "" ? {} : (JSON.parse(draft.supplierAttributeSchema) as unknown);
  }
  return body;
}

/**
 * General > Layouts and routing (docs/11 §8.3, decision 2 of §24): the review warm-up of newly activated layout versions
 * (5 by default; a change also applies to Active versions still carrying the old value, the settled P4 demo sets 0),
 * the field agreement the replay needs, whether source-routed suppliers are layout-managed by default, the trusted
 * forwarder domains and the typed attribute schema. A TenantAdmin setting.
 */
export function LayoutSettingsCard({ data, features, canEdit }: { data: TenantSettings; features: { supplierLayouts: boolean; sourceRouting: boolean }; canEdit: boolean }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState(() => toDraft(data));
  const [schemaError, setSchemaError] = useState<string | null>(null);
  useEffect(() => setDraft(toDraft(data)), [data]);
  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => setDraft((c) => ({ ...c, [key]: value }));

  const save = useMutation({
    mutationFn: async () => {
      let body: Record<string, unknown>;
      try {
        body = layoutSettingsBody(data, draft, features);
        setSchemaError(null);
      } catch {
        setSchemaError(t("settings.layouts.schemaInvalid"));
        throw new Error("schema");
      }
      return unwrap(await client.PUT("/api/v1/settings", { body: body as never }));
    },
    onSuccess: (saved) => {
      queryClient.setQueryData(queryKeys.settings.get(), saved);
      void queryClient.invalidateQueries({ queryKey: queryKeys.supplierLayouts.all });
      toast.success(t("settings.general.saved"));
    },
    onError: (error) => {
      if (error instanceof ApiError) toast.error(error.title, { description: error.isValidation ? Object.values(error.fieldErrors).flat().join(" ") : (error.problem?.detail ?? undefined) });
    },
    meta: { silent: true },
  });

  return (
    <Card data-testid="layout-settings">
      <CardHeader>
        <CardTitle>{t("settings.layouts.title")}</CardTitle>
        <CardDescription>{t("settings.layouts.description")}</CardDescription>
      </CardHeader>
      <CardContent>
        <form
          className="flex flex-col gap-4"
          onSubmit={(event) => {
            event.preventDefault();
            save.mutate();
          }}
        >
          {features.supplierLayouts && (
            <div className="grid gap-3 sm:grid-cols-2">
              <div className="flex flex-col gap-1">
                <Label htmlFor="layout-warmup">{t("settings.layouts.warmup")}</Label>
                <Input id="layout-warmup" type="number" min={0} max={100} value={draft.layoutReviewWarmup} onChange={(e) => set("layoutReviewWarmup", e.target.value)} disabled={!canEdit} className="w-24 text-right tabular-nums" data-testid="layout-warmup-input" />
                <span className="text-xs text-muted-foreground">{t("settings.layouts.warmupHint")}</span>
              </div>
              <div className="flex flex-col gap-1">
                <Label htmlFor="layout-agreement">{t("settings.layouts.agreement")}</Label>
                <Input id="layout-agreement" type="number" min={50} max={100} value={draft.layoutAgreementPercent} onChange={(e) => set("layoutAgreementPercent", e.target.value)} disabled={!canEdit} className="w-24 text-right tabular-nums" />
                <span className="text-xs text-muted-foreground">{t("settings.layouts.agreementHint")}</span>
              </div>
            </div>
          )}
          {features.sourceRouting && (
            <>
              <div className="flex flex-row items-center justify-between rounded-lg border p-3">
                <div className="space-y-0.5">
                  <Label htmlFor="layouts-required-default">{t("settings.layouts.requiredByDefault")}</Label>
                  <p className="text-xs text-muted-foreground">{t("settings.layouts.requiredByDefaultHint")}</p>
                </div>
                <Switch id="layouts-required-default" checked={draft.layoutsRequiredByDefault} onCheckedChange={(c) => set("layoutsRequiredByDefault", c)} disabled={!canEdit} />
              </div>
              <div className="flex flex-col gap-1">
                <Label htmlFor="trusted-forwarders">{t("settings.layouts.trustedForwarders")}</Label>
                <Textarea id="trusted-forwarders" value={draft.trustedForwarderDomains} onChange={(e) => set("trustedForwarderDomains", e.target.value)} rows={2} disabled={!canEdit} placeholder="bureau.example" />
                <span className="text-xs text-muted-foreground">{t("settings.layouts.trustedForwardersHint")}</span>
              </div>
              <div className="flex flex-col gap-1">
                <Label htmlFor="attribute-schema">{t("settings.layouts.attributeSchema")}</Label>
                <Textarea
                  id="attribute-schema"
                  value={draft.supplierAttributeSchema}
                  onChange={(e) => set("supplierAttributeSchema", e.target.value)}
                  rows={6}
                  className="font-mono text-xs"
                  spellCheck={false}
                  disabled={!canEdit}
                  placeholder='{"properties":{"erpVendorId":{"type":"string","title":"ERP vendor id"}},"required":["erpVendorId"]}'
                  aria-invalid={!!schemaError}
                />
                <span className="text-xs text-muted-foreground">{t("settings.layouts.attributeSchemaHint")}</span>
                {schemaError && <span className="text-xs text-destructive">{schemaError}</span>}
              </div>
            </>
          )}
          {canEdit && (
            <div className="flex justify-end">
              <Button type="submit" disabled={save.isPending} data-testid="layout-settings-save">
                {save.isPending && <Loader2 className="animate-spin" aria-hidden />}
                {t("settings.save")}
              </Button>
            </div>
          )}
        </form>
      </CardContent>
    </Card>
  );
}
