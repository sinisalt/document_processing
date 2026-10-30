import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { toast } from "sonner";
import type { components } from "@docproc/api-client";
import { Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Checkbox, Form, FormControl, FormDescription, FormField, FormItem, FormLabel, FormMessage, Input, Skeleton, Switch } from "@docproc/ui";
import { isApiError, queryKeys, toNumber, unwrap, useApi } from "@docproc/app-shell";
import { translated } from "@/lib/validation";

type Settings = components["schemas"]["TenantSettingsDto"];
type Supplier = components["schemas"]["SupplierResponse"];

const schema = z.object({
  autoApproveConfidenceThreshold: z.coerce.number(translated("settings.validation.threshold")).min(0, translated("settings.validation.threshold")).max(1, translated("settings.validation.threshold")),
  retentionYears: z.coerce.number(translated("settings.validation.retention")).int(translated("settings.validation.retention")).min(1, translated("settings.validation.retention")).max(30, translated("settings.validation.retention")),
  shareExtractorProfiles: z.boolean(),
  alwaysReviewSupplierIds: z.array(z.string()),
});
type FormValues = z.infer<typeof schema>;

function toForm(settings: Settings): FormValues {
  return {
    autoApproveConfidenceThreshold: toNumber(settings.autoApproveConfidenceThreshold) ?? 0.8,
    retentionYears: toNumber(settings.retentionYears) ?? 10,
    shareExtractorProfiles: settings.shareExtractorProfiles,
    alwaysReviewSupplierIds: settings.alwaysReviewSupplierIds,
  };
}

function SettingsForm({ settings, suppliers }: { settings: Settings; suppliers: Supplier[] }) {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<z.input<typeof schema>, unknown, FormValues>({ resolver: zodResolver(schema), defaultValues: toForm(settings) });
  const mutation = useMutation({
    mutationFn: async (values: FormValues) => unwrap(await client.PUT("/api/v1/settings", { body: values })),
    meta: { silent: true },
    onSuccess: async (response) => {
      toast.success(t("settings.saved"));
      form.reset(toForm(response.settings));
      await queryClient.invalidateQueries({ queryKey: queryKeys.settings.all });
    },
    onError: (error) => {
      if (isApiError(error) && error.isValidation) {
        for (const [field, messages] of Object.entries(error.fieldErrors)) form.setError(field as keyof FormValues, { message: messages.join(" ") });
        return;
      }
      toast.error(isApiError(error) ? error.title : t("settings.saveFailed"), { description: isApiError(error) ? error.problem?.detail ?? undefined : undefined });
    },
  });
  const knownIds = new Set(suppliers.map((s) => s.id));

  return (
    <Form {...form}>
      <form onSubmit={form.handleSubmit((values) => mutation.mutate(values))} className="grid gap-6" noValidate>
        <div className="grid gap-4 sm:grid-cols-2">
          <FormField
            control={form.control}
            name="autoApproveConfidenceThreshold"
            render={({ field }) => (
              <FormItem>
                <FormLabel>{t("settings.threshold")}</FormLabel>
                <FormControl>
                  <Input type="number" step="0.01" min={0} max={1} {...field} value={String(field.value)} />
                </FormControl>
                <FormDescription>{t("settings.thresholdHint")}</FormDescription>
                <FormMessage />
              </FormItem>
            )}
          />
          <FormField
            control={form.control}
            name="retentionYears"
            render={({ field }) => (
              <FormItem>
                <FormLabel>{t("settings.retention")}</FormLabel>
                <FormControl>
                  <Input type="number" step="1" min={1} max={30} {...field} value={String(field.value)} />
                </FormControl>
                <FormDescription>{t("settings.retentionHint")}</FormDescription>
                <FormMessage />
              </FormItem>
            )}
          />
        </div>
        <FormField
          control={form.control}
          name="shareExtractorProfiles"
          render={({ field }) => (
            <FormItem className="flex items-center justify-between gap-4 rounded-md border p-3">
              <div>
                <FormLabel>{t("settings.shareProfiles")}</FormLabel>
                <FormDescription>{t("settings.shareProfilesHint")}</FormDescription>
              </div>
              <FormControl>
                <Switch checked={field.value} onCheckedChange={field.onChange} aria-label={t("settings.shareProfiles")} />
              </FormControl>
            </FormItem>
          )}
        />
        <FormField
          control={form.control}
          name="alwaysReviewSupplierIds"
          render={({ field }) => (
            <FormItem>
              <FormLabel>{t("settings.alwaysReviewSuppliers")}</FormLabel>
              <FormDescription>{t("settings.alwaysReviewSuppliersHint")}</FormDescription>
              {suppliers.length === 0 ? (
                <p className="text-sm text-muted-foreground">{t("settings.noSuppliers")}</p>
              ) : (
                <ul className="grid gap-2 sm:grid-cols-2">
                  {suppliers.map((supplier) => {
                    const checked = field.value.includes(supplier.id);
                    const id = `always-review-${supplier.id}`;
                    return (
                      <li key={supplier.id} className="flex items-center gap-2">
                        <Checkbox id={id} checked={checked} onCheckedChange={(next) => field.onChange(next === true ? [...field.value, supplier.id] : field.value.filter((v) => v !== supplier.id))} />
                        <label htmlFor={id} className="text-sm">
                          {supplier.name}
                        </label>
                      </li>
                    );
                  })}
                </ul>
              )}
              {field.value.some((id) => !knownIds.has(id)) && <p className="text-xs text-muted-foreground">{t("settings.unknownSupplierIds", { count: field.value.filter((id) => !knownIds.has(id)).length })}</p>}
              <FormMessage />
            </FormItem>
          )}
        />
        <div className="flex justify-end gap-2">
          <Button type="button" variant="outline" onClick={() => form.reset(toForm(settings))} disabled={!form.formState.isDirty}>
            {t("settings.reset")}
          </Button>
          <Button type="submit" disabled={mutation.isPending || !form.formState.isDirty} aria-busy={mutation.isPending}>
            {t("settings.save")}
          </Button>
        </div>
      </form>
    </Form>
  );
}

/** Review policy and retention (`GET/PUT /settings`, tenant scope). The supplier list feeds the always-review picker. */
export function SettingsTab() {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const settings = useQuery({ queryKey: queryKeys.settings.get(), queryFn: async () => unwrap(await client.GET("/api/v1/settings")) });
  const suppliers = useQuery({
    queryKey: queryKeys.suppliers.list({ limit: 200 }),
    queryFn: async () => unwrap(await client.GET("/api/v1/suppliers", { params: { query: { limit: 200 } } })),
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>{t("settings.title")}</CardTitle>
        <CardDescription>{t("settings.description")}</CardDescription>
      </CardHeader>
      <CardContent>
        {settings.data ? (
          <SettingsForm key={settings.dataUpdatedAt} settings={settings.data.settings} suppliers={suppliers.data?.items ?? []} />
        ) : (
          <div className="grid gap-3" aria-busy>
            <Skeleton className="h-9 w-full" />
            <Skeleton className="h-9 w-full" />
            <Skeleton className="h-16 w-full" />
          </div>
        )}
      </CardContent>
    </Card>
  );
}
