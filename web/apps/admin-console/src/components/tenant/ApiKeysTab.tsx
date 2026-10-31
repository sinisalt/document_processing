import { useMemo, useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { toast } from "sonner";
import { Copy, KeyRound, Plus } from "lucide-react";
import type { components } from "@docproc/api-client";
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
  DialogTrigger,
  Form,
  FormControl,
  FormField,
  FormItem,
  FormLabel,
  FormMessage,
  Input,
  Label,
} from "@docproc/ui";
import { ConfirmDialog, DataTable, createDataTableColumnHelper, formatDateTime, isApiError, queryKeys, unwrap, useApi, type DataTableColumns } from "@docproc/app-shell";
import { translated } from "@/lib/validation";

type ApiKey = components["schemas"]["ApiKeySummary"];
type CreatedApiKey = components["schemas"]["CreatedApiKey"];

/** `ApiKeyScopes.All` as the API spells them (lower case). */
export const apiKeyScopes = ["ingest", "read"] as const;

const schema = z.object({
  name: z.string().trim().min(1, translated("tenants.validation.required")).max(200),
  scopes: z.array(z.enum(apiKeyScopes)).min(1, translated("apiKeys.validation.scopes")),
});
type FormValues = z.infer<typeof schema>;

const helper = createDataTableColumnHelper<ApiKey>();
const EMPTY: ApiKey[] = [];

function CreateApiKeyDialog() {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const [created, setCreated] = useState<CreatedApiKey | null>(null);
  const form = useForm<FormValues>({ resolver: zodResolver(schema), defaultValues: { name: "", scopes: ["read"] } });
  const mutation = useMutation({
    mutationFn: async (values: FormValues) => unwrap(await client.POST("/api/v1/api-keys", { body: { name: values.name, scopes: [...values.scopes] } })),
    meta: { silent: true },
    onSuccess: async (key) => {
      setCreated(key);
      form.reset();
      await queryClient.invalidateQueries({ queryKey: queryKeys.apiKeys.all });
    },
    onError: (error) => {
      if (isApiError(error) && error.isValidation) {
        for (const [field, messages] of Object.entries(error.fieldErrors)) form.setError(field as keyof FormValues, { message: messages.join(" ") });
        return;
      }
      toast.error(isApiError(error) ? error.title : t("apiKeys.createFailed"), { description: isApiError(error) ? error.problem?.detail ?? undefined : undefined });
    },
  });

  const copy = async () => {
    if (!created) return;
    try {
      await navigator.clipboard.writeText(created.key);
      toast.success(t("apiKeys.copied"));
    } catch {
      toast.error(t("apiKeys.copyFailed"));
    }
  };

  const close = (next: boolean) => {
    setOpen(next);
    if (!next) setCreated(null);
  };

  return (
    <Dialog open={open} onOpenChange={close}>
      <DialogTrigger asChild>
        <Button size="sm">
          <Plus aria-hidden />
          {t("apiKeys.create")}
        </Button>
      </DialogTrigger>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{created ? t("apiKeys.createdTitle") : t("apiKeys.createTitle")}</DialogTitle>
          <DialogDescription>{created ? t("apiKeys.createdDescription") : t("apiKeys.createDescription")}</DialogDescription>
        </DialogHeader>
        {created ? (
          <div className="grid gap-4">
            <Alert>
              <KeyRound aria-hidden />
              <AlertTitle>{created.name}</AlertTitle>
              <AlertDescription>
                <code className="block break-all rounded bg-muted p-2 font-mono text-xs" data-testid="created-api-key">
                  {created.key}
                </code>
              </AlertDescription>
            </Alert>
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => void copy()}>
                <Copy aria-hidden />
                {t("apiKeys.copy")}
              </Button>
              <Button type="button" onClick={() => close(false)}>
                {t("common:actions.close")}
              </Button>
            </DialogFooter>
          </div>
        ) : (
          <Form {...form}>
            <form onSubmit={form.handleSubmit((values) => mutation.mutate(values))} className="grid gap-4" noValidate>
              <FormField
                control={form.control}
                name="name"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("apiKeys.name")}</FormLabel>
                    <FormControl>
                      <Input {...field} />
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
                    <FormLabel>{t("apiKeys.scopes")}</FormLabel>
                    <ul className="flex gap-4">
                      {apiKeyScopes.map((scope) => {
                        const id = `api-key-scope-${scope}`;
                        const checked = field.value.includes(scope);
                        return (
                          <li key={scope} className="flex items-center gap-2">
                            <Checkbox id={id} checked={checked} onCheckedChange={(next) => field.onChange(next === true ? [...field.value, scope] : field.value.filter((s) => s !== scope))} />
                            <Label htmlFor={id} className="font-normal">
                              {t(`apiKeys.scope.${scope}`)}
                            </Label>
                          </li>
                        );
                      })}
                    </ul>
                    <FormMessage />
                  </FormItem>
                )}
              />
              <DialogFooter>
                <Button type="button" variant="outline" onClick={() => close(false)}>
                  {t("common:dialog.cancel")}
                </Button>
                <Button type="submit" disabled={mutation.isPending} aria-busy={mutation.isPending}>
                  {t("apiKeys.create")}
                </Button>
              </DialogFooter>
            </form>
          </Form>
        )}
      </DialogContent>
    </Dialog>
  );
}

/** The tenant's API keys (`/api-keys`, tenant scope): list, create (the key is shown once), revoke. */
export function ApiKeysTab() {
  const { t, i18n } = useTranslation("admin");
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [revoking, setRevoking] = useState<ApiKey | null>(null);

  const keys = useQuery({ queryKey: queryKeys.apiKeys.list(), queryFn: async () => unwrap(await client.GET("/api/v1/api-keys")) });

  const revoke = useMutation({
    mutationFn: async (key: ApiKey) => unwrap(await client.DELETE("/api/v1/api-keys/{id}", { params: { path: { id: key.id } } })),
    onSuccess: async (_data, key) => {
      toast.success(t("apiKeys.revoked", { name: key.name }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.apiKeys.all });
    },
  });

  const columns = useMemo<DataTableColumns<ApiKey>>(
    () =>
      helper.columns([
        helper.accessor("name", { header: t("apiKeys.name"), cell: (info) => <span className="font-medium">{info.getValue()}</span> }),
        helper.accessor("prefix", { header: t("apiKeys.prefix"), cell: (info) => <span className="font-mono text-xs">{info.getValue()}…</span> }),
        helper.accessor("scopes", {
          header: t("apiKeys.scopes"),
          enableSorting: false,
          cell: (info) => (
            <span className="flex gap-1">
              {info.getValue().map((scope) => (
                <Badge key={scope} variant="outline">
                  {t(`apiKeys.scope.${scope}`, { defaultValue: scope })}
                </Badge>
              ))}
            </span>
          ),
        }),
        helper.accessor("createdAt", { header: t("apiKeys.createdAt"), sortFn: "datetime", cell: (info) => <span className="whitespace-nowrap">{formatDateTime(info.getValue(), i18n.language)}</span> }),
        helper.accessor("lastUsedAt", { header: t("apiKeys.lastUsedAt"), sortFn: "datetime", cell: (info) => <span className="whitespace-nowrap">{formatDateTime(info.getValue(), i18n.language)}</span> }),
        helper.accessor("revokedAt", {
          header: t("apiKeys.status"),
          cell: (info) => (info.getValue() ? <Badge variant="secondary">{t("apiKeys.revokedAt", { at: formatDateTime(info.getValue(), i18n.language) })}</Badge> : <Badge>{t("apiKeys.active")}</Badge>),
        }),
        helper.display({
          id: "actions",
          header: () => <span className="sr-only">{t("apiKeys.actions")}</span>,
          cell: (info) =>
            info.row.original.revokedAt ? null : (
              <div className="flex justify-end">
                <Button variant="outline" size="sm" onClick={() => setRevoking(info.row.original)} aria-label={t("apiKeys.revokeAria", { name: info.row.original.name })}>
                  {t("apiKeys.revoke")}
                </Button>
              </div>
            ),
        }),
      ]),
    [t, i18n.language],
  );

  return (
    <>
      <DataTable
        columns={columns}
        data={keys.data ?? EMPTY}
        getRowId={(row) => row.id}
        isLoading={keys.isPending} query={keys}
        toolbar={<CreateApiKeyDialog />}
        caption={t("apiKeys.caption")}
        emptyTitle={t("apiKeys.empty")}
        emptyDescription={t("apiKeys.emptyDescription")}
      />
      {revoking && (
        <ConfirmDialog
          open
          onOpenChange={(open) => !open && setRevoking(null)}
          destructive
          title={t("apiKeys.revokeTitle", { name: revoking.name })}
          description={t("apiKeys.revokeDescription")}
          confirmLabel={t("apiKeys.revoke")}
          isPending={revoke.isPending}
          onConfirm={() => revoke.mutateAsync(revoking)}
        />
      )}
    </>
  );
}
