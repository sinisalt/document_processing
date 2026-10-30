import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Ban, Check, Copy, KeyRound, Loader2, Plus } from "lucide-react";
import {
  Alert,
  AlertDescription,
  AlertTitle,
  Badge,
  Button,
  Checkbox,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  Form,
  FormControl,
  FormDescription,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
  Input,
} from "@docproc/ui";
import { ConfirmDialog, DataTable, createDataTableColumnHelper, formatDateTime, queryKeys, relativeTime, unwrap, useApi } from "@docproc/app-shell";
import { EMPTY_ARRAY, type ApiKey } from "@/lib/queries";

/** `ApiKeyScopes` of the API: what a machine client may do. */
export const apiKeyScopes = ["ingest", "read"] as const;

const schema = z.object({
  name: z.string().trim().min(1).max(100),
  scopes: z.array(z.enum(apiKeyScopes)).min(1),
});
type KeyForm = z.infer<typeof schema>;

const helper = createDataTableColumnHelper<ApiKey>();

/** API keys: list, create (the key is shown once in a copy dialog), revoke. TenantAdmin only. */
export function ApiKeysTab() {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [createOpen, setCreateOpen] = useState(false);
  const [created, setCreated] = useState<{ key: string; name: string } | null>(null);
  const [copied, setCopied] = useState(false);
  const [revoking, setRevoking] = useState<ApiKey | null>(null);

  const keys = useQuery({ queryKey: queryKeys.apiKeys.list(), queryFn: async () => unwrap(await client.GET("/api/v1/api-keys")) });
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: queryKeys.apiKeys.all });

  const form = useForm<KeyForm>({ resolver: zodResolver(schema), defaultValues: { name: "", scopes: ["ingest"] } });
  const create = useMutation({
    mutationFn: async (values: KeyForm) => unwrap(await client.POST("/api/v1/api-keys", { body: values })),
    onSuccess: (key) => {
      invalidate();
      form.reset();
      setCreateOpen(false);
      setCopied(false);
      setCreated({ key: key.key, name: key.name });
    },
  });
  const revoke = useMutation({
    mutationFn: async (id: string) => unwrap(await client.DELETE("/api/v1/api-keys/{id}", { params: { path: { id } } })),
    onSuccess: () => {
      toast.success(t("settings.apiKeys.revoked"));
      invalidate();
    },
  });

  const copy = async () => {
    if (!created) return;
    try {
      await navigator.clipboard.writeText(created.key);
      setCopied(true);
      toast.success(t("settings.apiKeys.copied"));
    } catch {
      toast.error(t("settings.apiKeys.copyFailed"));
    }
  };

  const columns = useMemo(
    () =>
      helper.columns([
        helper.accessor("name", { header: t("settings.apiKeys.columns.name"), cell: (info) => <span className="font-medium">{info.getValue()}</span> }),
        helper.accessor("prefix", { header: t("settings.apiKeys.columns.prefix"), cell: (info) => <code className="text-xs">{info.getValue()}…</code> }),
        helper.accessor((key) => key.scopes.join(", "), {
          id: "scopes",
          header: t("settings.apiKeys.columns.scopes"),
          cell: ({ row }) => (
            <span className="flex gap-1">
              {row.original.scopes.map((scope) => (
                <Badge key={scope} variant="outline">
                  {t(`settings.apiKeys.scopes.${scope}`, { defaultValue: scope })}
                </Badge>
              ))}
            </span>
          ),
        }),
        helper.accessor((key) => (key.revokedAt ? "revoked" : "active"), {
          id: "status",
          header: t("settings.apiKeys.columns.status"),
          cell: ({ row }) => (row.original.revokedAt ? <Badge variant="secondary">{t("settings.apiKeys.statusRevoked")}</Badge> : <Badge>{t("settings.apiKeys.statusActive")}</Badge>),
        }),
        helper.accessor("lastUsedAt", { header: t("settings.apiKeys.columns.lastUsed"), cell: (info) => (info.getValue() ? relativeTime(info.getValue(), i18n.language) : t("settings.apiKeys.neverUsed")), sortFn: "datetime" }),
        helper.accessor("createdAt", { header: t("settings.apiKeys.columns.created"), cell: (info) => formatDateTime(info.getValue(), i18n.language), sortFn: "datetime" }),
        helper.display({
          id: "actions",
          header: "",
          cell: ({ row }) =>
            row.original.revokedAt ? null : (
              <Button size="xs" variant="ghost" onClick={() => setRevoking(row.original)} aria-label={t("settings.apiKeys.revokeLabel", { name: row.original.name })}>
                <Ban aria-hidden />
                {t("settings.apiKeys.revoke")}
              </Button>
            ),
        }),
      ]),
    [t, i18n.language],
  );

  return (
    <div className="flex flex-col gap-4">
      <div className="flex justify-end">
        <Dialog open={createOpen} onOpenChange={setCreateOpen}>
          <Button onClick={() => setCreateOpen(true)}>
            <Plus aria-hidden />
            {t("settings.apiKeys.create")}
          </Button>
          <DialogContent>
            <DialogHeader>
              <DialogTitle>{t("settings.apiKeys.createTitle")}</DialogTitle>
              <DialogDescription>{t("settings.apiKeys.createDescription")}</DialogDescription>
            </DialogHeader>
            <Form {...form}>
              <form id="create-key-form" onSubmit={form.handleSubmit((values) => create.mutate(values))} className="flex flex-col gap-4">
                <FormField
                  control={form.control}
                  name="name"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("settings.apiKeys.fields.name")}</FormLabel>
                      <FormControl>
                        <Input {...field} autoFocus placeholder={t("settings.apiKeys.fields.namePlaceholder")} />
                      </FormControl>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <FormField
                  control={form.control}
                  name="scopes"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("settings.apiKeys.fields.scopes")}</FormLabel>
                      <div className="flex flex-col gap-2" role="group" aria-label={t("settings.apiKeys.fields.scopes")}>
                        {apiKeyScopes.map((scope) => (
                          <label key={scope} className="flex items-start gap-2 text-sm">
                            <Checkbox
                              checked={field.value.includes(scope)}
                              onCheckedChange={(checked) => field.onChange(checked === true ? [...field.value, scope] : field.value.filter((s) => s !== scope))}
                            />
                            <span>
                              <span className="font-medium">{t(`settings.apiKeys.scopes.${scope}`)}</span>
                              <FormDescription>{t(`settings.apiKeys.scopeHints.${scope}`)}</FormDescription>
                            </span>
                          </label>
                        ))}
                      </div>
                      <FormMessage />
                    </FormItem>
                  )}
                />
              </form>
            </Form>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setCreateOpen(false)}>
                {t("common:dialog.cancel")}
              </Button>
              <Button type="submit" form="create-key-form" disabled={create.isPending} aria-busy={create.isPending}>
                {create.isPending && <Loader2 className="animate-spin" aria-hidden />}
                {t("settings.apiKeys.create")}
              </Button>
            </DialogFooter>
          </DialogContent>
        </Dialog>
      </div>

      <DataTable columns={columns} data={keys.data ?? EMPTY_ARRAY} getRowId={(row) => row.id} isLoading={keys.isPending} caption={t("settings.apiKeys.caption")} emptyTitle={t("settings.apiKeys.empty")} emptyDescription={t("settings.apiKeys.emptyDescription")} />

      <Dialog open={created !== null} onOpenChange={(open) => !open && setCreated(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{t("settings.apiKeys.createdTitle", { name: created?.name })}</DialogTitle>
            <DialogDescription>{t("settings.apiKeys.createdDescription")}</DialogDescription>
          </DialogHeader>
          <Alert>
            <KeyRound aria-hidden />
            <AlertTitle>{t("settings.apiKeys.oneTime")}</AlertTitle>
            <AlertDescription>
              <code className="break-all rounded bg-muted px-2 py-1 font-mono text-xs select-all" data-testid="created-api-key">
                {created?.key}
              </code>
            </AlertDescription>
          </Alert>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={() => void copy()}>
              {copied ? <Check aria-hidden /> : <Copy aria-hidden />}
              {copied ? t("settings.apiKeys.copied") : t("settings.apiKeys.copy")}
            </Button>
            <Button type="button" onClick={() => setCreated(null)}>
              {t("common:actions.close")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <ConfirmDialog
        open={revoking !== null}
        onOpenChange={(open) => !open && setRevoking(null)}
        title={t("settings.apiKeys.revokeTitle")}
        description={t("settings.apiKeys.revokeDescription", { name: revoking?.name })}
        confirmLabel={t("settings.apiKeys.revoke")}
        destructive
        onConfirm={() => (revoking ? revoke.mutateAsync(revoking.id).then(() => undefined) : undefined)}
        isPending={revoke.isPending}
      />
    </div>
  );
}
