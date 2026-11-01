import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Pause, Play, Plus } from "lucide-react";
import {
  Badge,
  Button,
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
  Textarea,
} from "@docproc/ui";
import { ApiError, DataTable, createDataTableColumnHelper, formatDateTime, queryKeys, relativeTime, unwrap, useApi } from "@docproc/app-shell";
import { EMPTY_ARRAY, type Channel } from "@/lib/queries";
import { SectionIntro } from "./SectionIntro";

/** `ChannelType` of the API; the create form offers the three a tenant sets up itself (portal upload is implicit). */
export const channelTypes = ["ApiUpload", "Mailbox", "WatchedFolder", "PortalUpload"] as const;

/** Starting points for the type-specific `config` object. */
const configTemplates: Record<(typeof channelTypes)[number], string> = {
  ApiUpload: "{}",
  Mailbox: JSON.stringify({ address: "invoices@example.com", host: "imap.example.com", port: 993, username: "invoices@example.com", folder: "INBOX" }, null, 2),
  WatchedFolder: JSON.stringify({ path: "/mnt/scans/incoming", pattern: "*.pdf" }, null, 2),
  PortalUpload: "{}",
};

const schema = z.object({
  name: z.string().trim().min(1).max(100),
  type: z.enum(channelTypes),
  config: z.string().refine(
    (text) => {
      try {
        const parsed: unknown = JSON.parse(text || "{}");
        return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed);
      } catch {
        return false;
      }
    },
    { message: "json" },
  ),
  secret: z.string(),
});
type ChannelForm = z.infer<typeof schema>;

const helper = createDataTableColumnHelper<Channel>();

/** Channels: list, create (api / mailbox / folder with a config JSON), pause / resume. */
export function ChannelsTab({ canEdit }: { canEdit: boolean }) {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [createOpen, setCreateOpen] = useState(false);

  const channels = useQuery({ queryKey: queryKeys.channels.list(), queryFn: async () => unwrap(await client.GET("/api/v1/channels")) });
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: queryKeys.channels.all });

  const form = useForm<ChannelForm>({ resolver: zodResolver(schema), defaultValues: { name: "", type: "ApiUpload", config: "{}", secret: "" } });
  const create = useMutation({
    mutationFn: async (values: ChannelForm) =>
      unwrap(
        await client.POST("/api/v1/channels", {
          body: { name: values.name, type: values.type, config: JSON.parse(values.config || "{}") as unknown, secret: values.secret || null },
        }),
      ),
    onSuccess: (channel) => {
      toast.success(t("settings.channels.created", { name: channel.name }));
      invalidate();
      form.reset();
      setCreateOpen(false);
    },
    onError: (error) => {
      if (error instanceof ApiError && error.isValidation) {
        for (const [key, messages] of Object.entries(error.fieldErrors)) {
          const name = (key.charAt(0).toLowerCase() + key.slice(1)) as keyof ChannelForm;
          if (name in form.getValues()) form.setError(name, { type: "server", message: messages.join(" ") });
        }
      }
    },
  });
  const setStatus = useMutation({
    mutationFn: async ({ id, status }: { id: string; status: "Active" | "Paused" }) =>
      unwrap(await client.PATCH("/api/v1/channels/{id}", { params: { path: { id } }, body: { status, name: null, config: null, secret: null } })),
    onSuccess: (channel) => {
      toast.success(channel.status === "Paused" ? t("settings.channels.paused", { name: channel.name }) : t("settings.channels.resumed", { name: channel.name }));
      invalidate();
    },
  });

  const columns = useMemo(
    () =>
      helper.columns([
        helper.accessor("name", { header: t("settings.channels.columns.name"), cell: (info) => <span className="font-medium">{info.getValue()}</span> }),
        helper.accessor("type", { header: t("settings.channels.columns.type"), cell: (info) => t(`settings.channels.types.${info.getValue()}`, { defaultValue: info.getValue() }) }),
        helper.accessor("status", {
          header: t("settings.channels.columns.status"),
          cell: (info) => (
            <Badge variant={info.getValue() === "Active" ? "default" : info.getValue() === "Error" ? "destructive" : "secondary"} data-status={info.getValue()}>
              {t(`settings.channels.statuses.${info.getValue()}`, { defaultValue: info.getValue() })}
            </Badge>
          ),
        }),
        helper.accessor("hasSecret", { header: t("settings.channels.columns.secret"), cell: (info) => (info.getValue() ? t("settings.channels.secretSet") : "—") }),
        helper.accessor("lastPolledAt", { header: t("settings.channels.columns.lastPolled"), cell: (info) => (info.getValue() ? relativeTime(info.getValue(), i18n.language) : "—"), sortFn: "datetime" }),
        helper.accessor("lastError", { header: t("settings.channels.columns.lastError"), cell: (info) => (info.getValue() ? <span className="text-destructive">{info.getValue()}</span> : "—") }),
        helper.accessor("createdAt", { header: t("settings.channels.columns.created"), cell: (info) => formatDateTime(info.getValue(), i18n.language), sortFn: "datetime" }),
        ...(canEdit
          ? [
              helper.display({
                id: "actions",
                header: "",
                cell: ({ row }) => {
                  const channel = row.original;
                  const paused = channel.status === "Paused";
                  return (
                    <Button
                      size="xs"
                      variant="ghost"
                      onClick={() => setStatus.mutate({ id: channel.id, status: paused ? "Active" : "Paused" })}
                      disabled={setStatus.isPending}
                      aria-label={paused ? t("settings.channels.resumeLabel", { name: channel.name }) : t("settings.channels.pauseLabel", { name: channel.name })}
                    >
                      {paused ? <Play aria-hidden /> : <Pause aria-hidden />}
                      {paused ? t("settings.channels.resume") : t("settings.channels.pause")}
                    </Button>
                  );
                },
              }),
            ]
          : []),
      ]),
    [t, i18n.language, canEdit, setStatus],
  );

  const type = form.watch("type");

  return (
    <div className="flex flex-col gap-4">
      <SectionIntro
        title={t("settings.channels.title")}
        description={t("settings.channels.description")}
        action={
          canEdit && (
        <div className="flex justify-end">
          <Dialog open={createOpen} onOpenChange={setCreateOpen}>
            <Button onClick={() => setCreateOpen(true)}>
              <Plus aria-hidden />
              {t("settings.channels.create")}
            </Button>
            <DialogContent className="sm:max-w-lg">
              <DialogHeader>
                <DialogTitle>{t("settings.channels.createTitle")}</DialogTitle>
                <DialogDescription>{t("settings.channels.createDescription")}</DialogDescription>
              </DialogHeader>
              <Form {...form}>
                <form id="create-channel-form" onSubmit={form.handleSubmit((values) => create.mutate(values))} className="flex flex-col gap-4">
                  <FormField
                    control={form.control}
                    name="name"
                    render={({ field }) => (
                      <FormItem>
                        <FormLabel>{t("settings.channels.fields.name")}</FormLabel>
                        <FormControl>
                          <Input {...field} autoFocus />
                        </FormControl>
                        <FormMessage />
                      </FormItem>
                    )}
                  />
                  <FormField
                    control={form.control}
                    name="type"
                    render={({ field }) => (
                      <FormItem>
                        <FormLabel>{t("settings.channels.fields.type")}</FormLabel>
                        <Select
                          value={field.value}
                          onValueChange={(value) => {
                            field.onChange(value);
                            if (!form.formState.dirtyFields.config) form.setValue("config", configTemplates[value as (typeof channelTypes)[number]]);
                          }}
                        >
                          <FormControl>
                            <SelectTrigger>
                              <SelectValue />
                            </SelectTrigger>
                          </FormControl>
                          <SelectContent>
                            {channelTypes.map((value) => (
                              <SelectItem key={value} value={value}>
                                {t(`settings.channels.types.${value}`)}
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
                        <FormLabel>{t("settings.channels.fields.config")}</FormLabel>
                        <FormControl>
                          <Textarea {...field} rows={6} className="font-mono text-xs" spellCheck={false} />
                        </FormControl>
                        <FormDescription>{t(`settings.channels.configHints.${type}`)}</FormDescription>
                        <FormMessage>{form.formState.errors.config ? t("settings.channels.configInvalid") : undefined}</FormMessage>
                      </FormItem>
                    )}
                  />
                  <FormField
                    control={form.control}
                    name="secret"
                    render={({ field }) => (
                      <FormItem>
                        <FormLabel>{t("settings.channels.fields.secret")}</FormLabel>
                        <FormControl>
                          <Input type="password" autoComplete="off" {...field} />
                        </FormControl>
                        <FormDescription>{t("settings.channels.fields.secretHint")}</FormDescription>
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
                <Button type="submit" form="create-channel-form" disabled={create.isPending} aria-busy={create.isPending}>
                  {create.isPending && <Loader2 className="animate-spin" aria-hidden />}
                  {t("settings.channels.create")}
                </Button>
              </DialogFooter>
            </DialogContent>
          </Dialog>
        </div>
          )
        }
      />
      <DataTable columns={columns} data={channels.data ?? EMPTY_ARRAY} getRowId={(row) => row.id} isLoading={channels.isPending} query={channels} caption={t("settings.channels.caption")} emptyTitle={t("settings.channels.empty")} emptyDescription={t("settings.channels.emptyDescription")} />
    </div>
  );
}
