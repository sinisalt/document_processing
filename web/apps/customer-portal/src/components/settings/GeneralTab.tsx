import { useEffect } from "react";
import { useTranslation } from "react-i18next";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2 } from "lucide-react";
import { Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Checkbox, Form, FormControl, FormDescription, FormField, FormItem, FormLabel, FormMessage, Input, Label, Skeleton, Switch } from "@docproc/ui";
import { ApiError, formatPercent, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { useSuppliers, useTenantSettings, type TenantSettings } from "@/lib/queries";

const schema = z.object({
  autoApproveConfidenceThreshold: z.number().min(0).max(1),
  retentionYears: z.number().int().min(1).max(30),
  shareExtractorProfiles: z.boolean(),
  alwaysReviewSupplierIds: z.array(z.string()),
});
type SettingsForm = z.infer<typeof schema>;

function toForm(data: TenantSettings | undefined): SettingsForm {
  return {
    autoApproveConfidenceThreshold: Number(data?.settings.autoApproveConfidenceThreshold ?? 0.8),
    retentionYears: Number(data?.settings.retentionYears ?? 10),
    shareExtractorProfiles: data?.settings.shareExtractorProfiles ?? false,
    alwaysReviewSupplierIds: data?.settings.alwaysReviewSupplierIds ?? [],
  };
}

/** General: the tenant identity (read-only; platform staff change it) and the review policy / retention (`PUT /settings`). */
export function GeneralTab({ canEdit }: { canEdit: boolean }) {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const settings = useTenantSettings();
  const suppliers = useSuppliers({ limit: 200 });
  const form = useForm<SettingsForm>({ resolver: zodResolver(schema), defaultValues: toForm(settings.data) });

  useEffect(() => {
    if (settings.data) form.reset(toForm(settings.data));
  }, [settings.data, form]);

  const save = useMutation({
    mutationFn: async (values: SettingsForm) => unwrap(await client.PUT("/api/v1/settings", { body: values })),
    onSuccess: (saved) => {
      queryClient.setQueryData(queryKeys.settings.get(), saved);
      toast.success(t("settings.general.saved"));
    },
    onError: (error) => {
      if (error instanceof ApiError && error.isValidation) {
        for (const [key, messages] of Object.entries(error.fieldErrors)) {
          const name = (key.charAt(0).toLowerCase() + key.slice(1)) as keyof SettingsForm;
          if (name in form.getValues()) form.setError(name, { type: "server", message: messages.join(" ") });
        }
      }
    },
  });

  if (settings.isPending) {
    return <Skeleton className="h-64" aria-busy />;
  }
  const tenant = settings.data?.tenant;

  return (
    // items-start: each card as tall as its content, so the short identity card leaves no empty block beside the policy.
    <div className="grid grid-cols-1 items-start gap-6 lg:grid-cols-2">
      <Card>
        <CardHeader>
          <CardTitle>{t("settings.general.identityTitle")}</CardTitle>
          <CardDescription>{t("settings.general.identityDescription")}</CardDescription>
        </CardHeader>
        <CardContent>
          {tenant && (
            <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm">
              {(["name", "legalName", "slug", "vatId", "countryCode", "defaultCurrency", "timeZone"] as const).map((key) => (
                <div key={key} className="contents">
                  <dt className="text-muted-foreground">{t(`settings.general.identity.${key}`)}</dt>
                  <dd>{key === "countryCode" ? countryName(tenant.countryCode, i18n.language) : tenant[key] ?? "—"}</dd>
                </div>
              ))}
            </dl>
          )}
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>{t("settings.general.policyTitle")}</CardTitle>
          <CardDescription>{t("settings.general.policyDescription")}</CardDescription>
        </CardHeader>
        <CardContent>
          <Form {...form}>
            <form onSubmit={form.handleSubmit((values) => save.mutate(values))} className="flex flex-col gap-4">
              <FormField
                control={form.control}
                name="autoApproveConfidenceThreshold"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("settings.general.threshold")}</FormLabel>
                    <FormControl>
                      {/* Edited in percent like the hint below it and the review screen; stored as the API's 0..1 ratio. */}
                      <div className="flex items-center gap-2">
                        <Input
                          type="number"
                          step="1"
                          min={0}
                          max={100}
                          inputMode="numeric"
                          name={field.name}
                          ref={field.ref}
                          onBlur={field.onBlur}
                          value={ratioToPercentText(field.value)}
                          onChange={(event) => field.onChange(percentTextToRatio(event.target.value))}
                          disabled={!canEdit}
                          className="w-24 text-right tabular-nums"
                        />
                        <span className="text-sm text-muted-foreground" aria-hidden>
                          %
                        </span>
                      </div>
                    </FormControl>
                    <FormDescription>{t("settings.general.thresholdHint", { value: formatPercent(form.watch("autoApproveConfidenceThreshold"), i18n.language) })}</FormDescription>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="retentionYears"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("settings.general.retention")}</FormLabel>
                    <FormControl>
                      <Input type="number" min={1} max={30} name={field.name} ref={field.ref} onBlur={field.onBlur} value={Number.isNaN(field.value) ? "" : String(field.value)} onChange={(event) => field.onChange(event.target.value === "" ? Number.NaN : Number(event.target.value))} disabled={!canEdit} className="w-32" />
                    </FormControl>
                    <FormDescription>{t("settings.general.retentionHint")}</FormDescription>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="shareExtractorProfiles"
                render={({ field }) => (
                  <FormItem className="flex flex-row items-center justify-between rounded-lg border p-3">
                    <div className="space-y-0.5">
                      <FormLabel>{t("settings.general.shareProfiles")}</FormLabel>
                      <FormDescription>{t("settings.general.shareProfilesHint")}</FormDescription>
                    </div>
                    <FormControl>
                      <Switch checked={field.value} onCheckedChange={field.onChange} disabled={!canEdit} aria-label={t("settings.general.shareProfiles")} />
                    </FormControl>
                  </FormItem>
                )}
              />
              <FormField
                control={form.control}
                name="alwaysReviewSupplierIds"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("settings.general.alwaysReview")}</FormLabel>
                    <FormDescription>{t("settings.general.alwaysReviewHint")}</FormDescription>
                    <div className="max-h-48 overflow-y-auto rounded-md border p-2" role="group" aria-label={t("settings.general.alwaysReview")}>
                      {suppliers.items.length === 0 && <p className="text-sm text-muted-foreground">{t("settings.general.noSuppliers")}</p>}
                      {suppliers.items.map((supplier) => {
                        const checked = field.value.includes(supplier.id);
                        return (
                          <Label key={supplier.id} className="py-1 font-normal">
                            <Checkbox
                              checked={checked}
                              disabled={!canEdit}
                              onCheckedChange={(next) => field.onChange(next === true ? [...field.value, supplier.id] : field.value.filter((id) => id !== supplier.id))}
                            />
                            {supplier.name}
                          </Label>
                        );
                      })}
                    </div>
                    <FormMessage />
                  </FormItem>
                )}
              />
              {canEdit && (
                <div className="flex justify-end">
                  <Button type="submit" disabled={save.isPending || !form.formState.isDirty} aria-busy={save.isPending}>
                    {save.isPending && <Loader2 className="animate-spin" aria-hidden />}
                    {t("settings.save")}
                  </Button>
                </div>
              )}
            </form>
          </Form>
        </CardContent>
      </Card>
    </div>
  );
}

/** 0.8 → "80" (two decimals at most); NaN (an emptied input) → "". */
export function ratioToPercentText(ratio: number): string {
  if (Number.isNaN(ratio)) return "";
  return String(Math.round(ratio * 10000) / 100);
}

/** "80" → 0.8; "" → NaN, which the form's schema reports as required. */
export function percentTextToRatio(text: string): number {
  if (text.trim() === "") return Number.NaN;
  return Number(text) / 100;
}

/** "DE" → "Germany" / "Deutschland" in the UI language; an unknown code is shown as is. */
export function countryName(code: string | null | undefined, language: string): string {
  if (!code) return "—";
  try {
    const name = new Intl.DisplayNames([language], { type: "region" }).of(code.toUpperCase());
    return name && name !== code.toUpperCase() ? `${name} (${code.toUpperCase()})` : code;
  } catch {
    return code;
  }
}
