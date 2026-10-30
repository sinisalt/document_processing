import { useMemo, useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { toast } from "sonner";
import { Plus, Trash2 } from "lucide-react";
import type { components } from "@docproc/api-client";
import {
  Badge,
  Button,
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
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
  Textarea,
} from "@docproc/ui";
import { ConfirmDialog, DataTable, createDataTableColumnHelper, formatDateTime, isApiError, queryKeys, unwrap, useApi, type DataTableColumns } from "@docproc/app-shell";
import { JsonBlock } from "../KeyValue";

type Channel = components["schemas"]["ChannelResponse"];

/** `ChannelType` and `ChannelStatus` as the API spells them. */
export const channelTypes = ["Mailbox", "WatchedFolder", "ApiUpload", "PortalUpload"] as const;

const schema = z.object({
  name: z.string().trim().min(1, "required").max(200),
  type: z.enum(channelTypes),
  config: z.string().trim().refine((value) => value === "" || isJsonObject(value), "json"),
  secret: z.string().max(4096),
});
type FormValues = z.infer<typeof schema>;

function isJsonObject(value: string): boolean {
  try {
    const parsed: unknown = JSON.parse(value);
    return typeof parsed === "object" && parsed !== null;
  } catch {
    return false;
  }
}

const helper = createDataTableColumnHelper<Channel>();
const EMPTY: Channel[] = [];

function CreateChannelDialog() {
  const { t } = useTranslation("admin");
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [open, setOpen] = useState(false);
  const form = useForm<FormValues>({ resolver: zodResolver(schema), defaultValues: { name: "", type: "Mailbox", config: "", secret: "" } });
  const mutation = useMutation({
    mutationFn: async (values: FormValues) =>
      unwrap(
        await client.POST("/api/v1/channels", {
          body: { name: values.name, type: values.type, config: values.config ? (JSON.parse(values.config) as unknown) : null, secret: values.secret ? values.secret : null },
        }),
      ),
    meta: { silent: true },
    onSuccess: async (created) => {
      toast.success(t("channels.created", { name: created.name }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.channels.all });
      form.reset();
      setOpen(false);
    },
    onError: (error) => {
      if (isApiError(error) && error.isValidation) {
        for (const [field, messages] of Object.entries(error.fieldErrors)) form.setError(field as keyof FormValues, { message: messages.join(" ") });
        return;
      }
      toast.error(isApiError(error) ? error.title : t("channels.createFailed"), { description: isApiError(error) ? error.problem?.detail ?? undefined : undefined });
    },
  });
  const message = (code: string | undefined) => (code === "required" ? t("tenants.validation.required") : code === "json" ? t("channels.validation.json") : code);

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button size="sm">
          <Plus aria-hidden />
          {t("channels.create")}
        </Button>
      </DialogTrigger>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{t("channels.createTitle")}</DialogTitle>
          <DialogDescription>{t("channels.createDescription")}</DialogDescription>
        </DialogHeader>
        <Form {...form}>
          <form onSubmit={form.handleSubmit((values) => mutation.mutate(values))} className="grid gap-4" noValidate>
            <FormField
              control={form.control}
              name="name"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("channels.name")}</FormLabel>
                  <FormControl>
                    <Input {...field} />
                  </FormControl>
                  <FormMessage>{message(form.formState.errors.name?.message)}</FormMessage>
                </FormItem>
              )}
            />
            <FormField
              control={form.control}
              name="type"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("channels.type")}</FormLabel>
                  <Select value={field.value} onValueChange={field.onChange}>
                    <FormControl>
                      <SelectTrigger className="w-full">
                        <SelectValue />
                      </SelectTrigger>
                    </FormControl>
                    <SelectContent>
                      {channelTypes.map((type) => (
                        <SelectItem key={type} value={type}>
                          {t(`channels.types.${type}`)}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                  <FormMessage />
                </FormItem>
              )}
            />
            <FormField
              control={form.control}
              name="config"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("channels.config")}</FormLabel>
                  <FormControl>
                    <Textarea {...field} rows={4} className="font-mono text-xs" placeholder='{ "address": "invoices@example.com" }' />
                  </FormControl>
                  <FormDescription>{t("channels.configHint")}</FormDescription>
                  <FormMessage>{message(form.formState.errors.config?.message)}</FormMessage>
                </FormItem>
              )}
            />
            <FormField
              control={form.control}
              name="secret"
              render={({ field }) => (
                <FormItem>
                  <FormLabel>{t("channels.secret")}</FormLabel>
                  <FormControl>
                    <Input type="password" autoComplete="off" {...field} />
                  </FormControl>
                  <FormDescription>{t("channels.secretHint")}</FormDescription>
                  <FormMessage />
                </FormItem>
              )}
            />
            <DialogFooter>
              <Button type="button" variant="outline" onClick={() => setOpen(false)}>
                {t("common:dialog.cancel")}
              </Button>
              <Button type="submit" disabled={mutation.isPending} aria-busy={mutation.isPending}>
                {t("channels.create")}
              </Button>
            </DialogFooter>
          </form>
        </Form>
      </DialogContent>
    </Dialog>
  );
}

/** The tenant's ingestion channels (`/channels`, tenant scope): list, create, pause/resume, delete. */
export function ChannelsTab() {
  const { t, i18n } = useTranslation("admin");
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [deleting, setDeleting] = useState<Channel | null>(null);
  const [inspecting, setInspecting] = useState<Channel | null>(null);

  const channels = useQuery({ queryKey: queryKeys.channels.list(), queryFn: async () => unwrap(await client.GET("/api/v1/channels")) });

  const setStatus = useMutation({
    mutationFn: async ({ channel, status }: { channel: Channel; status: "Active" | "Paused" }) =>
      unwrap(await client.PATCH("/api/v1/channels/{id}", { params: { path: { id: channel.id } }, body: { name: null, status, config: null, secret: null } })),
    onSuccess: async (updated) => {
      toast.success(updated.status === "Paused" ? t("channels.paused", { name: updated.name }) : t("channels.resumed", { name: updated.name }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.channels.all });
    },
  });

  const remove = useMutation({
    mutationFn: async (channel: Channel) => unwrap(await client.DELETE("/api/v1/channels/{id}", { params: { path: { id: channel.id } } })),
    onSuccess: async (_data, channel) => {
      toast.success(t("channels.deleted", { name: channel.name }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.channels.all });
    },
  });

  const columns = useMemo<DataTableColumns<Channel>>(
    () =>
      helper.columns([
        helper.accessor("name", {
          header: t("channels.name"),
          cell: (info) => (
            <Button variant="link" className="h-auto p-0 font-medium" onClick={() => setInspecting(info.row.original)}>
              {info.getValue()}
            </Button>
          ),
        }),
        helper.accessor("type", { header: t("channels.type"), cell: (info) => t(`channels.types.${info.getValue()}`, { defaultValue: info.getValue() }) }),
        helper.accessor("status", {
          header: t("channels.status"),
          cell: (info) => (
            <Badge variant={info.getValue() === "Active" ? "default" : info.getValue() === "Error" ? "destructive" : "secondary"} data-status={info.getValue()}>
              {t(`channels.statuses.${info.getValue()}`, { defaultValue: info.getValue() })}
            </Badge>
          ),
        }),
        helper.accessor("hasSecret", { header: t("channels.hasSecret"), cell: (info) => (info.getValue() ? t("yes") : t("no")) }),
        helper.accessor("lastPolledAt", { header: t("channels.lastPolledAt"), sortingFn: "datetime", cell: (info) => <span className="whitespace-nowrap">{formatDateTime(info.getValue(), i18n.language)}</span> }),
        helper.accessor("lastError", { header: t("channels.lastError"), cell: (info) => <span className="block max-w-xs truncate text-destructive" title={info.getValue() ?? undefined}>{info.getValue() ?? "—"}</span> }),
        helper.display({
          id: "actions",
          header: () => <span className="sr-only">{t("channels.actions")}</span>,
          cell: (info) => {
            const channel = info.row.original;
            return (
              <div className="flex justify-end gap-1">
                <Button
                  variant="outline"
                  size="sm"
                  disabled={setStatus.isPending}
                  onClick={() => setStatus.mutate({ channel, status: channel.status === "Paused" ? "Active" : "Paused" })}
                  aria-label={channel.status === "Paused" ? t("channels.resumeAria", { name: channel.name }) : t("channels.pauseAria", { name: channel.name })}
                >
                  {channel.status === "Paused" ? t("channels.resume") : t("channels.pause")}
                </Button>
                <Button variant="ghost" size="icon-sm" onClick={() => setDeleting(channel)} aria-label={t("channels.deleteAria", { name: channel.name })}>
                  <Trash2 aria-hidden />
                </Button>
              </div>
            );
          },
        }),
      ]),
    [t, i18n.language, setStatus],
  );

  return (
    <>
      <DataTable
        columns={columns}
        data={channels.data ?? EMPTY}
        getRowId={(row) => row.id}
        isLoading={channels.isPending}
        toolbar={<CreateChannelDialog />}
        caption={t("channels.caption")}
        emptyTitle={t("channels.empty")}
        emptyDescription={t("channels.emptyDescription")}
      />
      {deleting && (
        <ConfirmDialog
          open
          onOpenChange={(open) => !open && setDeleting(null)}
          destructive
          title={t("channels.deleteTitle", { name: deleting.name })}
          description={t("channels.deleteDescription")}
          confirmLabel={t("channels.delete")}
          isPending={remove.isPending}
          onConfirm={() => remove.mutateAsync(deleting)}
        />
      )}
      <Dialog open={inspecting !== null} onOpenChange={(open) => !open && setInspecting(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{inspecting?.name}</DialogTitle>
            <DialogDescription>{t("channels.configOf", { type: inspecting ? t(`channels.types.${inspecting.type}`, { defaultValue: inspecting.type }) : "" })}</DialogDescription>
          </DialogHeader>
          <JsonBlock value={inspecting?.config ?? null} />
        </DialogContent>
      </Dialog>
    </>
  );
}
