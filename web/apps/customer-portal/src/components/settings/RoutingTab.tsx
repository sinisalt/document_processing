import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Mail, FolderInput, Pencil } from "lucide-react";
import { Badge, Button, Checkbox, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, Input, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@docproc/ui";
import { ApiError, DataTable, createDataTableColumnHelper, queryKeys, relativeTime, unwrap, useApi } from "@docproc/app-shell";
import { localized, useDocumentTypes } from "@docproc/review";
import { RoutingTestBox } from "@/components/routing/RoutingTestBox";
import { EMPTY_ARRAY, useSuppliers, type Channel } from "@/lib/queries";
import { splitLines, useReceivingEntities } from "@/lib/routingQueries";
import { SectionIntro } from "./SectionIntro";

const NONE = "__none__";
const EMPTY_ID = "00000000-0000-0000-0000-000000000000";
const helper = createDataTableColumnHelper<Channel>();

export type SourceChannelType = "Mailbox" | "WatchedFolder";

export interface ChannelDraft {
  type: SourceChannelType;
  name: string;
  address: string;
  host: string;
  port: string;
  security: string;
  username: string;
  folder: string;
  unwrapForwards: boolean;
  trustedForwarderDomains: string;
  secret: string;
  path: string;
  pattern: string;
  stableSeconds: string;
  defaultSupplierId: string;
  defaultDocumentTypeKey: string;
  defaultReceivingEntityId: string;
}

export const emptyChannel = (type: SourceChannelType): ChannelDraft => ({
  type,
  name: "",
  address: "",
  host: "",
  port: "",
  security: NONE,
  username: "",
  folder: "",
  unwrapForwards: true,
  trustedForwarderDomains: "",
  secret: "",
  path: "",
  pattern: "",
  stableSeconds: "",
  defaultSupplierId: NONE,
  defaultDocumentTypeKey: NONE,
  defaultReceivingEntityId: NONE,
});

/**
 * A typed channel form as `config` (docs/05 "Channels"): a Mailbox `{address, host?, port?, security?, username?, folder?,
 * unwrapForwards?, trustedForwarderDomains?}` (without a host the platform's server is read), a WatchedFolder `{path?,
 * pattern?, stableSeconds?}`; what is left empty is not sent.
 */
export function channelConfig(draft: ChannelDraft): Record<string, unknown> {
  const config: Record<string, unknown> = {};
  const put = (key: string, value: string) => {
    if (value.trim() !== "") config[key] = value.trim();
  };
  if (draft.type === "Mailbox") {
    put("address", draft.address);
    put("host", draft.host);
    if (draft.port.trim() !== "") config.port = Number(draft.port);
    if (draft.security !== NONE) config.security = draft.security;
    put("username", draft.username);
    put("folder", draft.folder);
    if (!draft.unwrapForwards) config.unwrapForwards = false;
    const domains = splitLines(draft.trustedForwarderDomains);
    if (domains.length > 0) config.trustedForwarderDomains = domains;
  } else {
    put("path", draft.path);
    put("pattern", draft.pattern);
    if (draft.stableSeconds.trim() !== "") config.stableSeconds = Number(draft.stableSeconds);
  }
  return config;
}

/** A channel's place in words: its address or its folder. */
function channelPlace(channel: Channel): string {
  const config = (channel.config ?? {}) as { address?: string; path?: string; host?: string };
  return channel.type === "Mailbox" ? [config.address, config.host].filter(Boolean).join(" @ ") : (config.path ?? "/");
}

/**
 * Settings > Routing (docs/11 §19 item 5, §6.3, §7): the mail and folder channels the worker polls, with typed forms (the
 * mailbox address, server, folder and forward unwrapping; the watched folder below the tenant's drop zone) and their
 * default supplier, type and receiving entity; each channel's source key, last poll and last error; and the Test
 * routing box against the tenant's source rules.
 */
export function RoutingTab({ canEdit }: { canEdit: boolean }) {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const channels = useQuery({ queryKey: queryKeys.channels.list(), queryFn: async () => unwrap(await client.GET("/api/v1/channels")) });
  const sources = useMemo(() => (channels.data ?? []).filter((c) => c.type === "Mailbox" || c.type === "WatchedFolder"), [channels.data]);
  const suppliers = useSuppliers({ limit: 200 });
  const types = useDocumentTypes();
  const entities = useReceivingEntities();
  const [draft, setDraft] = useState<ChannelDraft | null>(null);
  const [editing, setEditing] = useState<Channel | null>(null);
  const set = <K extends keyof ChannelDraft>(key: K, value: ChannelDraft[K]) => setDraft((current) => (current ? { ...current, [key]: value } : current));
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: queryKeys.channels.all });
  const pick = (value: string) => (value === NONE ? null : value);

  const save = useMutation({
    mutationFn: async (value: ChannelDraft) => {
      const defaults = { defaultSupplierId: pick(value.defaultSupplierId), defaultDocumentTypeKey: pick(value.defaultDocumentTypeKey), defaultReceivingEntityId: pick(value.defaultReceivingEntityId) };
      if (editing) {
        return unwrap(
          await client.PATCH("/api/v1/channels/{id}", {
            params: { path: { id: editing.id } },
            body: {
              name: value.name.trim() || null,
              status: null,
              config: channelConfig(value) as never,
              secret: value.secret || null,
              defaultSupplierId: defaults.defaultSupplierId ?? EMPTY_ID,
              defaultDocumentTypeKey: defaults.defaultDocumentTypeKey ?? "",
              defaultReceivingEntityId: defaults.defaultReceivingEntityId ?? EMPTY_ID,
            },
          }),
        );
      }
      return unwrap(await client.POST("/api/v1/channels", { body: { name: value.name.trim(), type: value.type, config: channelConfig(value) as never, secret: value.secret || null, ...defaults } }));
    },
    onSuccess: (channel) => {
      toast.success(editing ? t("settings.routing.channelSaved", { name: channel.name }) : t("settings.channels.created", { name: channel.name }));
      setDraft(null);
      setEditing(null);
      invalidate();
    },
    onError: (error) => toast.error(error instanceof ApiError ? error.title : String(error), { description: error instanceof ApiError ? (error.isValidation ? Object.values(error.fieldErrors).flat().join(" ") : (error.problem?.detail ?? undefined)) : undefined }),
    meta: { silent: true },
  });

  const edit = (channel: Channel) => {
    const config = (channel.config ?? {}) as Record<string, unknown>;
    const text = (key: string) => (config[key] === undefined || config[key] === null ? "" : String(config[key]));
    setEditing(channel);
    setDraft({
      ...emptyChannel(channel.type as SourceChannelType),
      name: channel.name,
      address: text("address"),
      host: text("host"),
      port: text("port"),
      security: text("security") || NONE,
      username: text("username"),
      folder: text("folder"),
      unwrapForwards: config.unwrapForwards !== false,
      trustedForwarderDomains: Array.isArray(config.trustedForwarderDomains) ? (config.trustedForwarderDomains as string[]).join("\n") : "",
      path: text("path"),
      pattern: text("pattern"),
      stableSeconds: text("stableSeconds"),
      defaultSupplierId: channel.defaultSupplierId ?? NONE,
      defaultDocumentTypeKey: channel.defaultDocumentTypeKey ?? NONE,
      defaultReceivingEntityId: channel.defaultReceivingEntityId ?? NONE,
    });
  };

  const columns = useMemo(
    () =>
      helper.columns([
        helper.accessor("name", { header: t("settings.channels.columns.name"), cell: (info) => <span className="font-medium">{info.getValue()}</span> }),
        helper.accessor("type", { header: t("settings.channels.columns.type"), cell: (info) => t(`settings.channels.types.${info.getValue()}`, { defaultValue: info.getValue() }) }),
        helper.accessor((c) => channelPlace(c), { id: "place", header: t("settings.routing.columns.place"), cell: (info) => <span className="font-mono text-xs">{info.getValue()}</span> }),
        helper.accessor((c) => [c.defaultSupplierId ? (suppliers.items.find((s) => s.id === c.defaultSupplierId)?.name ?? "…") : null, c.defaultDocumentTypeKey].filter(Boolean).join(" · "), { id: "defaults", header: t("settings.routing.columns.defaults"), cell: (info) => info.getValue() || "—" }),
        helper.accessor("status", { header: t("settings.channels.columns.status"), cell: (info) => <Badge variant={info.getValue() === "Active" ? "default" : "secondary"}>{t(`settings.channels.statuses.${info.getValue()}`, { defaultValue: info.getValue() })}</Badge> }),
        helper.accessor("lastPolledAt", { header: t("settings.channels.columns.lastPolled"), cell: (info) => (info.getValue() ? relativeTime(info.getValue(), i18n.language) : "—") }),
        helper.accessor("lastError", { header: t("settings.channels.columns.lastError"), cell: (info) => (info.getValue() ? <span className="text-destructive">{info.getValue()}</span> : "—") }),
        ...(canEdit
          ? [
              helper.display({
                id: "edit",
                header: "",
                cell: ({ row }) => (
                  <Button size="xs" variant="ghost" onClick={() => edit(row.original)} aria-label={t("settings.routing.editLabel", { name: row.original.name })}>
                    <Pencil aria-hidden />
                    {t("settings.routing.edit")}
                  </Button>
                ),
              }),
            ]
          : []),
      ]),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [t, i18n.language, canEdit, suppliers.items],
  );

  return (
    <div className="flex flex-col gap-6" data-testid="routing-tab">
      <SectionIntro
        title={t("settings.routing.title")}
        description={t("settings.routing.description")}
        action={
          canEdit && (
            <div className="flex gap-2">
              <Button variant="outline" onClick={() => { setEditing(null); setDraft(emptyChannel("Mailbox")); }} data-testid="routing-add-mailbox">
                <Mail aria-hidden />
                {t("settings.routing.addMailbox")}
              </Button>
              <Button variant="outline" onClick={() => { setEditing(null); setDraft(emptyChannel("WatchedFolder")); }} data-testid="routing-add-folder">
                <FolderInput aria-hidden />
                {t("settings.routing.addFolder")}
              </Button>
            </div>
          )
        }
      />
      <DataTable columns={columns} data={sources.length ? sources : EMPTY_ARRAY} getRowId={(row) => row.id} isLoading={channels.isPending} query={channels} caption={t("settings.routing.caption")} emptyTitle={t("settings.routing.empty")} emptyDescription={t("settings.routing.emptyDescription")} />
      <RoutingTestBox channels={sources} />

      <Dialog open={draft !== null} onOpenChange={(o) => { if (!o) { setDraft(null); setEditing(null); } }}>
        <DialogContent className="sm:max-w-2xl">
          <DialogHeader>
            <DialogTitle>{draft?.type === "Mailbox" ? (editing ? t("settings.routing.editMailbox") : t("settings.routing.addMailbox")) : editing ? t("settings.routing.editFolder") : t("settings.routing.addFolder")}</DialogTitle>
            <DialogDescription>{draft?.type === "Mailbox" ? t("settings.routing.mailboxHint") : t("settings.routing.folderHint")}</DialogDescription>
          </DialogHeader>
          {draft && (
            <form
              id="routing-channel-form"
              className="grid max-h-[60vh] gap-3 overflow-y-auto sm:grid-cols-2"
              data-testid="routing-channel-form"
              onSubmit={(event) => {
                event.preventDefault();
                save.mutate(draft);
              }}
            >
              <div className="flex flex-col gap-1">
                <Label htmlFor="ch-name">{t("settings.channels.fields.name")}</Label>
                <Input id="ch-name" value={draft.name} onChange={(e) => set("name", e.target.value)} required data-testid="ch-name" />
              </div>
              {draft.type === "Mailbox" ? (
                <>
                  <div className="flex flex-col gap-1">
                    <Label htmlFor="ch-address">{t("settings.routing.fields.address")}</Label>
                    <Input id="ch-address" type="email" value={draft.address} onChange={(e) => set("address", e.target.value)} required placeholder="invoices@tenant-a.local" data-testid="ch-address" />
                  </div>
                  <div className="flex flex-col gap-1">
                    <Label htmlFor="ch-host">{t("settings.routing.fields.host")}</Label>
                    <Input id="ch-host" value={draft.host} onChange={(e) => set("host", e.target.value)} placeholder={t("settings.routing.fields.hostPlaceholder")} />
                  </div>
                  <div className="flex flex-col gap-1">
                    <Label htmlFor="ch-port">{t("settings.routing.fields.port")}</Label>
                    <Input id="ch-port" type="number" value={draft.port} onChange={(e) => set("port", e.target.value)} placeholder="993" className="w-28" />
                  </div>
                  <div className="flex flex-col gap-1">
                    <Label htmlFor="ch-security">{t("settings.routing.fields.security")}</Label>
                    <Select value={draft.security} onValueChange={(v) => set("security", v)}>
                      <SelectTrigger id="ch-security">
                        <SelectValue />
                      </SelectTrigger>
                      <SelectContent>
                        <SelectItem value={NONE}>{t("settings.routing.fields.securityDefault")}</SelectItem>
                        <SelectItem value="ssl">SSL/TLS</SelectItem>
                        <SelectItem value="starttls">STARTTLS</SelectItem>
                      </SelectContent>
                    </Select>
                  </div>
                  <div className="flex flex-col gap-1">
                    <Label htmlFor="ch-username">{t("settings.routing.fields.username")}</Label>
                    <Input id="ch-username" value={draft.username} onChange={(e) => set("username", e.target.value)} autoComplete="off" />
                  </div>
                  <div className="flex flex-col gap-1">
                    <Label htmlFor="ch-secret">{t("settings.channels.fields.secret")}</Label>
                    <Input id="ch-secret" type="password" value={draft.secret} onChange={(e) => set("secret", e.target.value)} autoComplete="new-password" placeholder={editing?.hasSecret ? t("settings.routing.fields.secretKept") : undefined} />
                  </div>
                  <div className="flex flex-col gap-1">
                    <Label htmlFor="ch-folder">{t("settings.routing.fields.folder")}</Label>
                    <Input id="ch-folder" value={draft.folder} onChange={(e) => set("folder", e.target.value)} placeholder="INBOX" />
                  </div>
                  <div className="flex flex-col gap-1 sm:col-span-2">
                    <Label htmlFor="ch-forwarders">{t("settings.routing.fields.trustedForwarderDomains")}</Label>
                    <Input id="ch-forwarders" value={draft.trustedForwarderDomains} onChange={(e) => set("trustedForwarderDomains", e.target.value)} placeholder="bureau.example" />
                  </div>
                  <div className="flex items-center gap-2 sm:col-span-2">
                    <Checkbox id="ch-unwrap" checked={draft.unwrapForwards} onCheckedChange={(c) => set("unwrapForwards", c === true)} />
                    <Label htmlFor="ch-unwrap" className="font-normal">
                      {t("settings.routing.fields.unwrapForwards")}
                    </Label>
                  </div>
                </>
              ) : (
                <>
                  <div className="flex flex-col gap-1">
                    <Label htmlFor="ch-path">{t("settings.routing.fields.path")}</Label>
                    <Input id="ch-path" value={draft.path} onChange={(e) => set("path", e.target.value)} placeholder="scanner" data-testid="ch-path" />
                  </div>
                  <div className="flex flex-col gap-1">
                    <Label htmlFor="ch-pattern">{t("settings.routing.fields.pattern")}</Label>
                    <Input id="ch-pattern" value={draft.pattern} onChange={(e) => set("pattern", e.target.value)} placeholder="*.pdf" />
                  </div>
                  <div className="flex flex-col gap-1">
                    <Label htmlFor="ch-stable">{t("settings.routing.fields.stableSeconds")}</Label>
                    <Input id="ch-stable" type="number" value={draft.stableSeconds} onChange={(e) => set("stableSeconds", e.target.value)} placeholder="5" className="w-28" />
                  </div>
                </>
              )}
              <div className="flex flex-col gap-1">
                <Label htmlFor="ch-supplier">{t("settings.routing.fields.defaultSupplier")}</Label>
                <Select value={draft.defaultSupplierId} onValueChange={(v) => set("defaultSupplierId", v)}>
                  <SelectTrigger id="ch-supplier">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value={NONE}>{t("settings.routing.fields.noDefault")}</SelectItem>
                    {suppliers.items.map((s) => (
                      <SelectItem key={s.id} value={s.id}>
                        {s.name}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
              <div className="flex flex-col gap-1">
                <Label htmlFor="ch-type">{t("settings.routing.fields.defaultType")}</Label>
                <Select value={draft.defaultDocumentTypeKey} onValueChange={(v) => set("defaultDocumentTypeKey", v)}>
                  <SelectTrigger id="ch-type">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value={NONE}>{t("settings.routing.fields.noDefault")}</SelectItem>
                    {(types.data ?? []).filter((type) => type.publishedVersion != null).map((type) => (
                      <SelectItem key={type.key} value={type.key}>
                        {localized(type.name, i18n.language) ?? type.key}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
              <div className="flex flex-col gap-1">
                <Label htmlFor="ch-entity">{t("settings.routing.fields.defaultEntity")}</Label>
                <Select value={draft.defaultReceivingEntityId} onValueChange={(v) => set("defaultReceivingEntityId", v)}>
                  <SelectTrigger id="ch-entity">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value={NONE}>{t("settings.routing.fields.noDefault")}</SelectItem>
                    {(entities.data ?? []).map((e) => (
                      <SelectItem key={e.id} value={e.id}>
                        {e.code} · {e.name}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
            </form>
          )}
          <DialogFooter>
            <Button variant="outline" onClick={() => { setDraft(null); setEditing(null); }}>
              {t("common:dialog.cancel")}
            </Button>
            <Button type="submit" form="routing-channel-form" disabled={save.isPending} data-testid="routing-channel-save">
              {save.isPending && <Loader2 className="animate-spin" aria-hidden />}
              {t("settings.save")}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
