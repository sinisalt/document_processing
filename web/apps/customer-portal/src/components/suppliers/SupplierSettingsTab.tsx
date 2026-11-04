import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2 } from "lucide-react";
import { Button, Input, Label, RadioGroup, RadioGroupItem, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Switch } from "@docproc/ui";
import { ApiError, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { localized, useDocumentTypes } from "@docproc/review";
import { useSupplierGroups } from "@/lib/routingQueries";
import { useTenantSettings, type Supplier } from "@/lib/queries";

const NONE = "__none__";
const EMPTY_GROUP = "00000000-0000-0000-0000-000000000000";

/** `settings` of a supplier (docs/05 "Suppliers"): the members the sheet edits. */
export interface SupplierSettingsValues {
  defaultDocumentTypeKey?: string;
  allowedTypeKeys?: string[];
  fixDocumentType?: "whenSourceMatched" | "always" | "never";
  locale?: { culture?: string; dateOrder?: "DMY" | "MDY" | "YMD" };
  review?: { mode?: "inherit" | "always"; autoApproveMinConfidence?: number };
  defaults?: { currency?: string };
  layouts?: { required?: boolean | "inherit" | "true" | "false" };
  requireSenderAuth?: boolean;
}

interface Draft {
  groupId: string;
  defaultDocumentTypeKey: string;
  allowedTypeKeys: string[];
  fixDocumentType: "whenSourceMatched" | "always" | "never";
  culture: string;
  dateOrder: string;
  reviewMode: "inherit" | "always";
  currency: string;
  layoutsRequired: "inherit" | "yes" | "no";
  requireSenderAuth: "inherit" | "yes" | "no";
}

export function settingsDraft(supplier: Supplier): Draft {
  const settings = (supplier.settings ?? {}) as SupplierSettingsValues;
  const required = settings.layouts?.required;
  return {
    groupId: supplier.groupId ?? NONE,
    defaultDocumentTypeKey: settings.defaultDocumentTypeKey ?? NONE,
    allowedTypeKeys: settings.allowedTypeKeys ?? [],
    fixDocumentType: settings.fixDocumentType ?? "whenSourceMatched",
    culture: settings.locale?.culture ?? "",
    dateOrder: settings.locale?.dateOrder ?? NONE,
    reviewMode: settings.review?.mode ?? "inherit",
    currency: settings.defaults?.currency ?? "",
    layoutsRequired: required === true || required === "true" ? "yes" : required === false || required === "false" ? "no" : "inherit",
    requireSenderAuth: settings.requireSenderAuth === true ? "yes" : settings.requireSenderAuth === false ? "no" : "inherit",
  };
}

/** The draft as the supplier's `settings` object: what is left at its default is not sent (PATCH replaces the settings). */
export function settingsBody(draft: Draft, stored: SupplierSettingsValues | null | undefined): SupplierSettingsValues {
  const next: SupplierSettingsValues = {};
  if (draft.defaultDocumentTypeKey !== NONE) next.defaultDocumentTypeKey = draft.defaultDocumentTypeKey;
  if (draft.allowedTypeKeys.length > 0) next.allowedTypeKeys = draft.allowedTypeKeys;
  if (draft.fixDocumentType !== "whenSourceMatched") next.fixDocumentType = draft.fixDocumentType;
  if (draft.culture.trim() || draft.dateOrder !== NONE) next.locale = { ...(draft.culture.trim() ? { culture: draft.culture.trim() } : {}), ...(draft.dateOrder !== NONE ? { dateOrder: draft.dateOrder as "DMY" } : {}) };
  const minConfidence = stored?.review?.autoApproveMinConfidence;
  if (draft.reviewMode !== "inherit" || minConfidence !== undefined) next.review = { mode: draft.reviewMode, ...(minConfidence !== undefined ? { autoApproveMinConfidence: minConfidence } : {}) };
  if (draft.currency.trim()) next.defaults = { currency: draft.currency.trim().toUpperCase() };
  if (draft.layoutsRequired !== "inherit") next.layouts = { required: draft.layoutsRequired === "yes" };
  if (draft.requireSenderAuth !== "inherit") next.requireSenderAuth = draft.requireSenderAuth === "yes";
  return next;
}

/**
 * The Suppliers sheet's Settings tab (docs/11 §19 item 3, §6.4): the supplier's group, its types (a default, the allowed
 * ones, when a single one is pinned at ingest), its locale, its review policy, its currency, whether it is
 * layout-managed ("Layouts required": inherit, showing the tenant default, yes or no) and whether its sender rules need
 * sender authentication. `PATCH /suppliers/{id} {settings, groupId}`.
 */
export function SupplierSettingsTab({ supplier, canEdit }: { supplier: Supplier; canEdit: boolean }) {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const types = useDocumentTypes();
  const groups = useSupplierGroups();
  const tenant = useTenantSettings();
  const [draft, setDraft] = useState<Draft>(() => settingsDraft(supplier));
  const set = <K extends keyof Draft>(key: K, value: Draft[K]) => setDraft((current) => ({ ...current, [key]: value }));
  const published = (types.data ?? []).filter((type) => type.publishedVersion != null);
  const tenantDefault = tenant.data?.settings?.layoutsRequiredByDefault === true;

  const save = useMutation({
    mutationFn: async () =>
      unwrap(
        await client.PATCH("/api/v1/suppliers/{id}", {
          params: { path: { id: supplier.id } },
          body: {
            name: null,
            vatId: null,
            taxId: null,
            ibans: null,
            emailDomains: null,
            aliases: null,
            defaultCurrency: null,
            alwaysReview: null,
            groupId: draft.groupId === NONE ? EMPTY_GROUP : draft.groupId,
            settings: settingsBody(draft, supplier.settings as SupplierSettingsValues) as never,
          },
        }),
      ),
    onSuccess: (saved) => {
      toast.success(t("suppliers.updated", { name: saved.name }));
      void queryClient.invalidateQueries({ queryKey: queryKeys.suppliers.all });
    },
    onError: (error) => {
      if (error instanceof ApiError) toast.error(error.title, { description: error.isValidation ? Object.values(error.fieldErrors).flat().join(" ") : (error.problem?.detail ?? undefined) });
    },
    meta: { silent: true },
  });

  return (
    <form
      className="flex flex-col gap-4"
      data-testid="supplier-settings"
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      <fieldset className="flex flex-col gap-2 rounded-lg border p-3">
        <legend className="px-1 text-sm font-medium">{t("suppliers.settings.layoutsRequired")}</legend>
        <p className="text-xs text-muted-foreground">{t("suppliers.settings.layoutsRequiredHint")}</p>
        <RadioGroup value={draft.layoutsRequired} onValueChange={(v) => set("layoutsRequired", v as Draft["layoutsRequired"])} disabled={!canEdit} className="flex flex-wrap gap-4" data-testid="layouts-required">
          {(["inherit", "yes", "no"] as const).map((value) => (
            <div key={value} className="flex items-center gap-2">
              <RadioGroupItem value={value} id={`layouts-required-${value}`} data-testid={`layouts-required-${value}`} />
              <Label htmlFor={`layouts-required-${value}`} className="font-normal">
                {value === "inherit" ? t("suppliers.settings.inheritTenant", { value: tenantDefault ? t("suppliers.settings.yes") : t("suppliers.settings.no") }) : t(`suppliers.settings.${value}`)}
              </Label>
            </div>
          ))}
        </RadioGroup>
      </fieldset>

      <div className="grid gap-3 sm:grid-cols-2">
        <div className="flex flex-col gap-1">
          <Label htmlFor="supplier-group">{t("suppliers.settings.group")}</Label>
          <Select value={draft.groupId} onValueChange={(v) => set("groupId", v)} disabled={!canEdit}>
            <SelectTrigger id="supplier-group">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={NONE}>{t("suppliers.settings.noGroup")}</SelectItem>
              {(groups.data ?? []).map((g) => (
                <SelectItem key={g.id} value={g.id}>
                  {g.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <div className="flex flex-col gap-1">
          <Label htmlFor="supplier-default-type">{t("suppliers.settings.defaultType")}</Label>
          <Select value={draft.defaultDocumentTypeKey} onValueChange={(v) => set("defaultDocumentTypeKey", v)} disabled={!canEdit}>
            <SelectTrigger id="supplier-default-type">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={NONE}>{t("suppliers.settings.noDefaultType")}</SelectItem>
              {published.map((type) => (
                <SelectItem key={type.key} value={type.key}>
                  {localized(type.name, i18n.language) ?? type.key}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <div className="flex flex-col gap-1">
          <Label htmlFor="supplier-fix-type">{t("suppliers.settings.fixDocumentType")}</Label>
          <Select value={draft.fixDocumentType} onValueChange={(v) => set("fixDocumentType", v as Draft["fixDocumentType"])} disabled={!canEdit}>
            <SelectTrigger id="supplier-fix-type">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {(["whenSourceMatched", "always", "never"] as const).map((v) => (
                <SelectItem key={v} value={v}>
                  {t(`suppliers.settings.fix.${v}`)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <div className="flex flex-col gap-1">
          <Label htmlFor="supplier-review-mode">{t("suppliers.settings.reviewMode")}</Label>
          <Select value={draft.reviewMode} onValueChange={(v) => set("reviewMode", v as Draft["reviewMode"])} disabled={!canEdit}>
            <SelectTrigger id="supplier-review-mode">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="inherit">{t("suppliers.settings.review.inherit")}</SelectItem>
              <SelectItem value="always">{t("suppliers.settings.review.always")}</SelectItem>
            </SelectContent>
          </Select>
        </div>
        <div className="flex flex-col gap-1">
          <Label htmlFor="supplier-culture">{t("suppliers.settings.culture")}</Label>
          <Input id="supplier-culture" value={draft.culture} onChange={(e) => set("culture", e.target.value)} placeholder="de-DE" disabled={!canEdit} className="w-32" />
        </div>
        <div className="flex flex-col gap-1">
          <Label htmlFor="supplier-date-order">{t("suppliers.settings.dateOrder")}</Label>
          <Select value={draft.dateOrder} onValueChange={(v) => set("dateOrder", v)} disabled={!canEdit}>
            <SelectTrigger id="supplier-date-order" className="w-32">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={NONE}>{t("suppliers.settings.fromCulture")}</SelectItem>
              {["DMY", "MDY", "YMD"].map((v) => (
                <SelectItem key={v} value={v}>
                  {v}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <div className="flex flex-col gap-1">
          <Label htmlFor="supplier-currency">{t("suppliers.settings.currency")}</Label>
          <Input id="supplier-currency" value={draft.currency} onChange={(e) => set("currency", e.target.value)} maxLength={3} placeholder="EUR" disabled={!canEdit} className="w-24 uppercase" />
        </div>
        <div className="flex flex-col gap-1">
          <Label htmlFor="supplier-sender-auth">{t("suppliers.settings.requireSenderAuth")}</Label>
          <Select value={draft.requireSenderAuth} onValueChange={(v) => set("requireSenderAuth", v as Draft["requireSenderAuth"])} disabled={!canEdit}>
            <SelectTrigger id="supplier-sender-auth">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="inherit">{t("suppliers.settings.senderAuthDefault")}</SelectItem>
              <SelectItem value="yes">{t("suppliers.settings.yes")}</SelectItem>
              <SelectItem value="no">{t("suppliers.settings.no")}</SelectItem>
            </SelectContent>
          </Select>
        </div>
      </div>

      <fieldset className="flex flex-col gap-2">
        <legend className="text-sm font-medium">{t("suppliers.settings.allowedTypes")}</legend>
        <div className="flex flex-wrap gap-3">
          {published.map((type) => (
            <Label key={type.key} className="flex items-center gap-2 text-sm font-normal">
              <Switch
                checked={draft.allowedTypeKeys.includes(type.key)}
                onCheckedChange={(on) => set("allowedTypeKeys", on ? [...draft.allowedTypeKeys, type.key] : draft.allowedTypeKeys.filter((k) => k !== type.key))}
                disabled={!canEdit}
              />
              {localized(type.name, i18n.language) ?? type.key}
            </Label>
          ))}
        </div>
        <span className="text-xs text-muted-foreground">{t("suppliers.settings.allowedTypesHint")}</span>
      </fieldset>

      {canEdit && (
        <div>
          <Button type="submit" disabled={save.isPending} data-testid="supplier-settings-save">
            {save.isPending && <Loader2 className="animate-spin" aria-hidden />}
            {t("suppliers.save")}
          </Button>
        </div>
      )}
    </form>
  );
}
