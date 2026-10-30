import { useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { toast } from "sonner";
import { Pencil } from "lucide-react";
import type { components } from "@docproc/api-client";
import { Button, Card, CardContent, CardDescription, CardHeader, CardTitle, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, Form, FormControl, FormDescription, FormField, FormItem, FormLabel, FormMessage, Input } from "@docproc/ui";
import { ConfirmDialog, Policies, TenantStatusBadge, formatDateTime, isApiError, queryKeys, unwrap, useApi, useAuth } from "@docproc/app-shell";
import { KeyValue } from "../KeyValue";

type Tenant = components["schemas"]["TenantResponse"];

const schema = z.object({
  name: z.string().trim().min(1, "required").max(200),
  legalName: z.string().trim().min(1, "required").max(300),
  vatId: z.string().trim().max(32),
  nameAliases: z.string().trim(),
});
type FormValues = z.infer<typeof schema>;

/** `"Alias one, alias two"` → `["Alias one", "alias two"]`; blanks dropped. */
export function parseAliases(value: string): string[] {
  return value
    .split(/[,\n]/)
    .map((alias) => alias.trim())
    .filter(Boolean);
}

function EditTenantDialog({ tenant, open, onOpenChange }: { tenant: Tenant; open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const queryClient = useQueryClient();
  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    values: { name: tenant.name, legalName: tenant.legalName, vatId: tenant.vatId ?? "", nameAliases: tenant.nameAliases.join(", ") },
  });
  const mutation = useMutation({
    mutationFn: async (values: FormValues) =>
      unwrap(
        await client.PATCH("/api/v1/tenants/{id}", {
          params: { path: { id: tenant.id } },
          // An empty VAT id clears it (PatchTenantRequest); status is not changed here (suspend/activate have their own actions).
          body: { name: values.name, legalName: values.legalName, vatId: values.vatId, nameAliases: parseAliases(values.nameAliases), status: null },
        }),
      ),
    meta: { silent: true },
    onSuccess: async (updated) => {
      toast.success(t("tenant.saved", { name: updated.name }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.tenants.all });
      onOpenChange(false);
    },
    onError: (error) => {
      if (isApiError(error) && error.isValidation) {
        for (const [field, messages] of Object.entries(error.fieldErrors)) form.setError(field as keyof FormValues, { message: messages.join(" ") });
        return;
      }
      toast.error(isApiError(error) ? error.title : t("tenant.saveFailed"), { description: isApiError(error) ? error.problem?.detail ?? undefined : undefined });
    },
  });
  const message = (code: string | undefined) => (code === "required" ? t("tenants.validation.required") : code);

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{t("tenant.editTitle")}</DialogTitle>
          <DialogDescription>{t("tenant.editDescription")}</DialogDescription>
        </DialogHeader>
        <Form {...form}>
          <form onSubmit={form.handleSubmit((values) => mutation.mutate(values))} className="grid gap-4" noValidate>
            <FormField
              control={form.control}
              name="name"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("tenants.name")}</FormLabel>
                  <FormControl>
                    <Input {...field} />
                  </FormControl>
                  <FormMessage>{message(form.formState.errors.name?.message)}</FormMessage>
                </FormItem>
              )}
            />
            <FormField
              control={form.control}
              name="legalName"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("tenants.legalName")}</FormLabel>
                  <FormControl>
                    <Input {...field} />
                  </FormControl>
                  <FormMessage>{message(form.formState.errors.legalName?.message)}</FormMessage>
                </FormItem>
              )}
            />
            <FormField
              control={form.control}
              name="vatId"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("tenants.vatId")}</FormLabel>
                  <FormControl>
                    <Input {...field} />
                  </FormControl>
                  <FormMessage />
                </FormItem>
              )}
            />
            <FormField
              control={form.control}
              name="nameAliases"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("tenant.aliases")}</FormLabel>
                  <FormControl>
                    <Input {...field} />
                  </FormControl>
                  <FormDescription>{t("tenant.aliasesHint")}</FormDescription>
                  <FormMessage />
                </FormItem>
              )}
            />
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
                {t("common:dialog.cancel")}
              </Button>
              <Button type="submit" disabled={mutation.isPending} aria-busy={mutation.isPending}>
                {t("tenant.save")}
              </Button>
            </DialogFooter>
          </form>
        </Form>
      </DialogContent>
    </Dialog>
  );
}

/** Identity of the tenant (read from `GET /tenants/{id}`, platform scope), edit, and the suspend/activate switch. */
export function OverviewTab({ tenant }: { tenant: Tenant }) {
  const { t, i18n } = useTranslation("admin");
  const { client } = useApi();
  const { hasPolicy } = useAuth();
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const canManage = hasPolicy(Policies.PlatformAdmin);
  const suspended = tenant.status === "Suspended";

  const status = useMutation({
    mutationFn: async () => {
      const path = { params: { path: { id: tenant.id } } };
      return suspended ? unwrap(await client.POST("/api/v1/tenants/{id}:activate", path)) : unwrap(await client.POST("/api/v1/tenants/{id}:suspend", path));
    },
    onSuccess: async (updated) => {
      toast.success(updated.status === "Active" ? t("tenant.activated", { name: updated.name }) : t("tenant.suspended", { name: updated.name }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.tenants.all });
    },
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          {t("tenant.identity")}
          <TenantStatusBadge status={tenant.status} />
        </CardTitle>
        <CardDescription>{t("tenant.identityDescription")}</CardDescription>
        {canManage && (
          <div className="flex flex-wrap gap-2 pt-2">
            <Button variant="outline" size="sm" onClick={() => setEditing(true)}>
              <Pencil aria-hidden />
              {t("tenant.edit")}
            </Button>
            {tenant.status !== "Closed" && (
              <Button variant={suspended ? "default" : "destructive"} size="sm" onClick={() => setConfirming(true)}>
                {suspended ? t("tenant.activate") : t("tenant.suspend")}
              </Button>
            )}
          </div>
        )}
      </CardHeader>
      <CardContent>
        <KeyValue
          columns={3}
          items={[
            { label: t("tenants.name"), value: tenant.name },
            { label: t("tenants.slug"), value: tenant.slug, mono: true },
            { label: t("tenants.legalName"), value: tenant.legalName },
            { label: t("tenants.vatId"), value: tenant.vatId ?? "—", mono: true },
            { label: t("tenants.currency"), value: tenant.defaultCurrency },
            { label: t("tenants.country"), value: tenant.countryCode },
            { label: t("tenants.timeZone"), value: tenant.timeZone },
            { label: t("tenant.aliases"), value: tenant.nameAliases.length ? tenant.nameAliases.join(", ") : "—" },
            { label: t("tenant.id"), value: tenant.id, mono: true },
            { label: t("tenant.createdAt"), value: formatDateTime(tenant.createdAt, i18n.language) },
            { label: t("tenant.updatedAt"), value: formatDateTime(tenant.updatedAt, i18n.language) },
          ]}
        />
      </CardContent>
      {editing && <EditTenantDialog tenant={tenant} open={editing} onOpenChange={setEditing} />}
      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        destructive={!suspended}
        title={suspended ? t("tenant.activateTitle", { name: tenant.name }) : t("tenant.suspendTitle", { name: tenant.name })}
        description={suspended ? t("tenant.activateDescription") : t("tenant.suspendDescription")}
        confirmLabel={suspended ? t("tenant.activate") : t("tenant.suspend")}
        isPending={status.isPending}
        onConfirm={async () => { await status.mutateAsync(); }}
      />
    </Card>
  );
}
