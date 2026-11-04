import { useEffect, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useForm, type UseFormReturn } from "react-hook-form";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Pencil, Plus, Trash2 } from "lucide-react";
import {
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
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
  Switch,
  Textarea,
} from "@docproc/ui";
import { ApiError, ConfirmDialog, DataTable, createDataTableColumnHelper, formatDateTime, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { EMPTY_ARRAY } from "@/lib/queries";
import { SectionIntro } from "@/components/settings/SectionIntro";
import { transports, useDestinations, type Destination, type Transport } from "./api";
import { authNeedsSecret, authTypes, configOf, formFieldOfConfigPath, formOf, httpMethods, type DestinationFormValues } from "./destinationForm";

const helper = createDataTableColumnHelper<Destination>();

/** The placeholders a URL or file-name template may use (docs/11 §13.3 as built), shown as written, not interpolated. */
const URL_PLACEHOLDERS = "{{meta.documentId}}, {{meta.version}}, {{meta.event}}, {{meta.typeKey}}, {{meta.deliveryId}}, {{output.<path>}}";

/** Where a destination sends: the URL of an http destination, the folder of a file drop, "pull feed" for a feed. */
export function destinationTarget(destination: Destination): string {
  const config = (destination.config ?? {}) as Record<string, unknown>;
  if (destination.transport === "http") return `${String(config.method ?? "POST")} ${String(config.urlTemplate ?? "")}`;
  if (destination.transport === "blob") return `${String(config.path ?? "")}/${String(config.fileNameTemplate ?? "")}`;
  return "";
}

/**
 * Settings > Integrations > Destinations (docs/11 §13.3, §19): the receivers, each with a typed form per transport
 * (http, file drop, pull feed), the write-only secret with its two-slot rotation, enable/disable and delete. A test
 * call (`:test`) is not built yet.
 */
export function DestinationsPanel() {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const destinations = useDestinations();
  const [editing, setEditing] = useState<Destination | "new" | null>(null);
  const [deleting, setDeleting] = useState<Destination | null>(null);
  const invalidate = () => queryClient.invalidateQueries({ queryKey: queryKeys.outputs.destinations() });

  const setStatus = useMutation({
    mutationFn: async ({ id, status }: { id: string; status: "Active" | "Disabled" }) =>
      unwrap(await client.PATCH("/api/v1/destinations/{id}", { params: { path: { id } }, body: { status, name: null, config: null } })),
    onSuccess: async (destination) => {
      toast.success(destination.status === "Active" ? t("integrations.destinations.enabled", { name: destination.name }) : t("integrations.destinations.disabled", { name: destination.name }));
      await invalidate();
    },
  });
  const remove = useMutation({
    mutationFn: async (id: string) => unwrap(await client.DELETE("/api/v1/destinations/{id}", { params: { path: { id } } })),
    onSuccess: async () => {
      toast.success(t("integrations.destinations.deleted"));
      setDeleting(null);
      await invalidate();
    },
  });

  const columns = useMemo(
    () =>
      helper.columns([
        helper.accessor("name", { header: t("integrations.destinations.columns.name"), cell: (info) => <span className="font-medium">{info.getValue()}</span> }),
        helper.accessor("transport", { header: t("integrations.destinations.columns.transport"), cell: (info) => t(`integrations.transports.${info.getValue()}`, { defaultValue: info.getValue() }) }),
        helper.display({ id: "target", header: t("integrations.destinations.columns.target"), cell: ({ row }) => <span className="font-mono text-xs break-all">{destinationTarget(row.original) || "—"}</span> }),
        helper.accessor("hasSecret", {
          header: t("integrations.destinations.columns.secret"),
          cell: ({ row }) =>
            row.original.hasSecret ? (
              <span title={row.original.secretSetAt ? formatDateTime(row.original.secretSetAt, i18n.language) : undefined}>
                {t("integrations.destinations.secretSet")}
                {row.original.hasPreviousSecret ? ` · ${t("integrations.destinations.previousSecret")}` : ""}
              </span>
            ) : (
              "—"
            ),
        }),
        helper.accessor("status", {
          header: t("integrations.destinations.columns.status"),
          cell: ({ row }) => (
            <div className="flex items-center gap-2">
              <Switch
                checked={row.original.status === "Active"}
                disabled={setStatus.isPending}
                onCheckedChange={(on) => setStatus.mutate({ id: row.original.id, status: on ? "Active" : "Disabled" })}
                aria-label={t("integrations.destinations.toggle", { name: row.original.name })}
              />
              <Badge variant={row.original.status === "Active" ? "success" : "secondary"}>{t(`integrations.destinations.statuses.${row.original.status}`, { defaultValue: row.original.status })}</Badge>
            </div>
          ),
        }),
        helper.display({
          id: "actions",
          header: "",
          cell: ({ row }) => (
            <div className="flex justify-end gap-1">
              <Button size="xs" variant="ghost" onClick={() => setEditing(row.original)} data-testid={`edit-destination-${row.original.name}`}>
                <Pencil aria-hidden />
                {t("integrations.edit")}
              </Button>
              <Button size="xs" variant="ghost" onClick={() => setDeleting(row.original)} aria-label={t("integrations.destinations.deleteLabel", { name: row.original.name })}>
                <Trash2 aria-hidden />
              </Button>
            </div>
          ),
        }),
      ]),
    [t, i18n.language, setStatus],
  );

  return (
    <div className="flex flex-col gap-4" data-testid="destinations-panel">
      <SectionIntro
        title={t("integrations.destinations.title")}
        description={t("integrations.destinations.description")}
        action={
          <Button onClick={() => setEditing("new")} data-testid="new-destination">
            <Plus aria-hidden />
            {t("integrations.destinations.create")}
          </Button>
        }
      />
      <DataTable
        columns={columns}
        data={destinations.data ?? EMPTY_ARRAY}
        getRowId={(row) => row.id}
        isLoading={destinations.isPending}
        query={destinations}
        caption={t("integrations.destinations.title")}
        emptyTitle={t("integrations.destinations.empty")}
        emptyDescription={t("integrations.destinations.emptyDescription")}
        rowAttributes={(row) => ({ "data-destination": row.name })}
      />
      <DestinationDialog destination={editing === "new" ? null : editing} open={editing !== null} onOpenChange={(open) => !open && setEditing(null)} onSaved={invalidate} />
      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={t("integrations.destinations.deleteTitle", { name: deleting?.name ?? "" })}
        description={t("integrations.destinations.deleteDescription")}
        confirmLabel={t("integrations.delete")}
        destructive
        isPending={remove.isPending}
        onConfirm={() => remove.mutateAsync(deleting!.id).then(() => undefined)}
      />
    </div>
  );
}

function DestinationDialog({ destination, open, onOpenChange, onSaved }: { destination: Destination | null; open: boolean; onOpenChange: (open: boolean) => void; onSaved: () => Promise<unknown> }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const form = useForm<DestinationFormValues>({ defaultValues: formOf(destination) });
  const [problem, setProblem] = useState<string | null>(null);

  useEffect(() => {
    if (open) {
      form.reset(formOf(destination));
      setProblem(null);
    }
  }, [open, destination, form]);

  const save = useMutation({
    mutationFn: async (values: DestinationFormValues) => {
      const built = configOf(values);
      if ("problem" in built) throw new Error(t(`integrations.destinations.problems.${built.problem}`));
      if (!destination) {
        return unwrap(await client.POST("/api/v1/destinations", { body: { name: values.name.trim(), transport: values.transport, config: built.config as never, secret: values.secret || null } }));
      }
      const saved = unwrap(await client.PATCH("/api/v1/destinations/{id}", { params: { path: { id: destination.id } }, body: { name: values.name.trim(), status: null, config: built.config as never } }));
      if (values.secret) unwrap(await client.PUT("/api/v1/destinations/{id}/secret", { params: { path: { id: destination.id } }, body: { secret: values.secret } }));
      return saved;
    },
    meta: { silent: true },
    onSuccess: async (saved) => {
      toast.success(destination ? t("integrations.destinations.updated", { name: saved.name }) : t("integrations.destinations.created", { name: saved.name }));
      await onSaved();
      onOpenChange(false);
    },
    onError: (error) => {
      if (error instanceof ApiError && error.isValidation) {
        const unplaced: string[] = [];
        for (const [path, messages] of Object.entries(error.fieldErrors)) {
          const field = formFieldOfConfigPath(path);
          if (field) form.setError(field, { type: "server", message: messages.join(" ") });
          else unplaced.push(`${path}: ${messages.join(" ")}`);
        }
        setProblem(unplaced.length ? unplaced.join(" ") : null);
        return;
      }
      setProblem(error instanceof ApiError ? (error.problem?.detail ?? error.title) : error instanceof Error ? error.message : String(error));
    },
  });

  const transport = form.watch("transport");
  const authType = form.watch("authType");

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>{destination ? t("integrations.destinations.editTitle", { name: destination.name }) : t("integrations.destinations.createTitle")}</DialogTitle>
          <DialogDescription>{t("integrations.destinations.formDescription")}</DialogDescription>
        </DialogHeader>
        <Form {...form}>
          <form id="destination-form" onSubmit={form.handleSubmit((values) => save.mutate(values))} className="grid gap-4" data-testid="destination-form">
            <div className="grid gap-4 sm:grid-cols-2">
              <TextField form={form} name="name" label={t("integrations.destinations.fields.name")} required />
              <FormField
                control={form.control}
                name="transport"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("integrations.destinations.fields.transport")}</FormLabel>
                    <Select value={field.value} onValueChange={(value) => field.onChange(value as Transport)} disabled={!!destination}>
                      <FormControl>
                        <SelectTrigger data-testid="destination-transport">
                          <SelectValue />
                        </SelectTrigger>
                      </FormControl>
                      <SelectContent>
                        {transports.map((value) => (
                          <SelectItem key={value} value={value}>
                            {t(`integrations.transports.${value}`)}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                    <FormDescription>{t(`integrations.destinations.transportHints.${field.value}`)}</FormDescription>
                  </FormItem>
                )}
              />
            </div>

            {transport === "http" && (
              <>
                <div className="grid gap-4 sm:grid-cols-[8rem_1fr]">
                  <MethodField form={form} name="method" label={t("integrations.destinations.fields.method")} />
                  <TextField form={form} name="urlTemplate" label={t("integrations.destinations.fields.urlTemplate")} placeholder="https://erp.example/api/article-prices" hint={t("integrations.destinations.hints.urlTemplate", { placeholders: URL_PLACEHOLDERS })} required mono />
                </div>
                <div className="grid gap-4 sm:grid-cols-2">
                  <TextField form={form} name="timeoutSeconds" label={t("integrations.destinations.fields.timeoutSeconds")} placeholder="30" />
                  <TextField form={form} name="successCodes" label={t("integrations.destinations.fields.successCodes")} placeholder="200, 201, 202" />
                </div>
                <FormField
                  control={form.control}
                  name="authType"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("integrations.destinations.fields.authType")}</FormLabel>
                      <Select value={field.value} onValueChange={field.onChange}>
                        <FormControl>
                          <SelectTrigger data-testid="destination-auth">
                            <SelectValue />
                          </SelectTrigger>
                        </FormControl>
                        <SelectContent>
                          {authTypes.map((value) => (
                            <SelectItem key={value} value={value}>
                              {t(`integrations.destinations.auth.${value}`)}
                            </SelectItem>
                          ))}
                        </SelectContent>
                      </Select>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                {authType === "apiKeyHeader" && <TextField form={form} name="headerName" label={t("integrations.destinations.fields.headerName")} placeholder="X-Api-Key" required />}
                {authType === "basic" && <TextField form={form} name="username" label={t("integrations.destinations.fields.username")} required />}
                {authType === "oauth2ClientCredentials" && (
                  <div className="grid gap-4 sm:grid-cols-3">
                    <TextField form={form} name="tokenUrl" label={t("integrations.destinations.fields.tokenUrl")} required mono />
                    <TextField form={form} name="clientId" label={t("integrations.destinations.fields.clientId")} required />
                    <TextField form={form} name="scope" label={t("integrations.destinations.fields.scope")} />
                  </div>
                )}
                <FormField
                  control={form.control}
                  name="headers"
                  render={({ field }) => (
                    <FormItem>
                      <FormLabel>{t("integrations.destinations.fields.headers")}</FormLabel>
                      <FormControl>
                        <Textarea {...field} rows={2} className="font-mono text-xs" spellCheck={false} placeholder="X-Mandant: 1000" />
                      </FormControl>
                      <FormDescription>{t("integrations.destinations.hints.headers")}</FormDescription>
                      <FormMessage />
                    </FormItem>
                  )}
                />
                <TextField form={form} name="responseMapping" label={t("integrations.destinations.fields.responseMapping")} placeholder="importId" hint={t("integrations.destinations.hints.responseMapping")} mono />
                <div className="grid gap-4 sm:grid-cols-[8rem_1fr]">
                  <MethodField form={form} name="updateMethod" label={t("integrations.destinations.fields.updateMethod")} optional />
                  <TextField form={form} name="updateUrlTemplate" label={t("integrations.destinations.fields.updateUrlTemplate")} placeholder="https://erp.example/api/article-prices/{{output.header.priceListId}}" hint={t("integrations.destinations.hints.updateUrlTemplate")} mono />
                </div>
                <FormField
                  control={form.control}
                  name="sendOriginal"
                  render={({ field }) => (
                    <FormItem className="flex flex-row items-center gap-2">
                      <FormControl>
                        <Checkbox checked={field.value} onCheckedChange={(on) => field.onChange(on === true)} />
                      </FormControl>
                      <FormLabel className="font-normal">{t("integrations.destinations.fields.sendOriginal")}</FormLabel>
                    </FormItem>
                  )}
                />
              </>
            )}

            {transport === "blob" && (
              <div className="grid gap-4 sm:grid-cols-2">
                <TextField form={form} name="path" label={t("integrations.destinations.fields.path")} placeholder="erp/inbox" required mono />
                <TextField form={form} name="fileNameTemplate" label={t("integrations.destinations.fields.fileNameTemplate")} placeholder="{{meta.documentId}}-{{meta.version}}.json" hint={t("integrations.destinations.hints.fileNameTemplate")} required mono />
              </div>
            )}

            {transport === "pull" && <TextField form={form} name="retentionDays" label={t("integrations.destinations.fields.retentionDays")} placeholder="30" hint={t("integrations.destinations.hints.pull")} />}

            {(transport === "http" ? authNeedsSecret(authType) : false) && (
              <FormField
                control={form.control}
                name="secret"
                render={({ field }) => (
                  <FormItem>
                    <FormLabel>{t("integrations.destinations.fields.secret")}</FormLabel>
                    <FormControl>
                      <Input type="password" autoComplete="off" {...field} data-testid="destination-secret" />
                    </FormControl>
                    <FormDescription>{destination?.hasSecret ? t("integrations.destinations.hints.secretKeep") : t("integrations.destinations.hints.secret")}</FormDescription>
                    <FormMessage />
                  </FormItem>
                )}
              />
            )}
            {problem && (
              <p className="text-sm text-destructive" role="alert" data-testid="destination-problem">
                {problem}
              </p>
            )}
          </form>
        </Form>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
            {t("common:dialog.cancel")}
          </Button>
          <Button type="submit" form="destination-form" disabled={save.isPending} aria-busy={save.isPending} data-testid="destination-save">
            {save.isPending && <Loader2 className="animate-spin" aria-hidden />}
            {destination ? t("integrations.save") : t("integrations.destinations.create")}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

type TextName = "name" | "urlTemplate" | "timeoutSeconds" | "successCodes" | "headerName" | "username" | "tokenUrl" | "clientId" | "scope" | "responseMapping" | "updateUrlTemplate" | "path" | "fileNameTemplate" | "retentionDays";

function TextField({ form, name, label, placeholder, hint, required, mono }: { form: UseFormReturn<DestinationFormValues>; name: TextName; label: string; placeholder?: string; hint?: string; required?: boolean; mono?: boolean }) {
  const { t } = useTranslation();
  return (
    <FormField
      control={form.control}
      name={name}
      rules={required ? { validate: (value) => (typeof value === "string" && value.trim() !== "") || t("integrations.required") } : undefined}
      render={({ field }) => (
        <FormItem>
          <FormLabel>{label}</FormLabel>
          <FormControl>
            <Input {...field} value={field.value} placeholder={placeholder} className={mono ? "font-mono text-xs" : undefined} spellCheck={false} data-testid={`destination-${name}`} />
          </FormControl>
          {hint && <FormDescription>{hint}</FormDescription>}
          <FormMessage />
        </FormItem>
      )}
    />
  );
}

const NO_METHOD = "__default";

function MethodField({ form, name, label, optional }: { form: UseFormReturn<DestinationFormValues>; name: "method" | "updateMethod"; label: string; optional?: boolean }) {
  const { t } = useTranslation();
  return (
    <FormField
      control={form.control}
      name={name}
      render={({ field }) => (
        <FormItem>
          <FormLabel>{label}</FormLabel>
          <Select value={field.value || NO_METHOD} onValueChange={(value) => field.onChange(value === NO_METHOD ? "" : value)}>
            <FormControl>
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
            </FormControl>
            <SelectContent>
              {optional && <SelectItem value={NO_METHOD}>{t("integrations.destinations.defaultMethod")}</SelectItem>}
              {httpMethods.map((value) => (
                <SelectItem key={value} value={value}>
                  {value}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <FormMessage />
        </FormItem>
      )}
    />
  );
}
