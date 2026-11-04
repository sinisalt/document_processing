import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Loader2, Plus, Trash2 } from "lucide-react";
import { Badge, Button, Checkbox, Input, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Skeleton, Switch } from "@docproc/ui";
import { ApiError, ConfirmDialog, EmptyState, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { localized, useDocumentTypes } from "@docproc/review";
import { RoutingTestBox } from "@/components/routing/RoutingTestBox";
import { useReceivingEntities, useSourceRules, sourceRuleBody, type SourceRule } from "@/lib/routingQueries";
import type { Supplier } from "@/lib/queries";

const NONE = "__none__";

interface RuleDraft {
  sender: string;
  senderDomain: string;
  recipient: string;
  channelId: string;
  folderPrefix: string;
  subjectRegex: string;
  fileNameRegex: string;
  mode: "Exclusive" | "DefaultWhenNoContentMatch";
  requireSenderAuth: boolean;
  priority: string;
  pinTypeKey: string;
  receivingEntityId: string;
  enabled: boolean;
}

const EMPTY: RuleDraft = { sender: "", senderDomain: "", recipient: "", channelId: NONE, folderPrefix: "", subjectRegex: "", fileNameRegex: "", mode: "Exclusive", requireSenderAuth: true, priority: "0", pinTypeKey: NONE, receivingEntityId: NONE, enabled: true };

/** The draft as `SourceRuleRequest` (docs/05 "Routing and supplier configuration endpoints as built"). */
export function ruleRequest(draft: RuleDraft) {
  return sourceRuleBody({
    ...draft,
    channelId: draft.channelId === NONE ? "" : draft.channelId,
    pinTypeKey: draft.pinTypeKey === NONE ? "" : draft.pinTypeKey,
    receivingEntityId: draft.receivingEntityId === NONE ? "" : draft.receivingEntityId,
    priority: Number(draft.priority),
  });
}

/**
 * The Suppliers sheet's Routing tab (docs/11 §19 item 3, §6.5, §7): the supplier's source rules in words with their
 * mode, priority and state (a rule a reviewer remembered in review is saved disabled, a proposal a ConfigAdmin enables),
 * a form for a new rule (all conditions must hold; sender rules need DMARC by default, decision 1), and the Test
 * routing box.
 */
export function SupplierRoutingTab({ supplier, canEdit }: { supplier: Supplier; canEdit: boolean }) {
  const { t, i18n } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const rules = useSourceRules(supplier.id);
  const channels = useQuery({ queryKey: queryKeys.channels.list(), queryFn: async () => unwrap(await client.GET("/api/v1/channels")) });
  const entities = useReceivingEntities();
  const types = useDocumentTypes();
  const [draft, setDraft] = useState<RuleDraft | null>(null);
  const [deleting, setDeleting] = useState<SourceRule | null>(null);
  const invalidate = () => void queryClient.invalidateQueries({ queryKey: queryKeys.routing.sourceRules(supplier.id) });

  const create = useMutation({
    mutationFn: async (value: RuleDraft) => unwrap(await client.POST("/api/v1/suppliers/{supplierId}/source-rules", { params: { path: { supplierId: supplier.id } }, body: ruleRequest(value) })),
    onSuccess: (rule) => {
      toast.success(t("suppliers.routing.created", { rule: rule.describe }));
      setDraft(null);
      invalidate();
    },
    onError: (error) => {
      if (error instanceof ApiError) toast.error(error.title, { description: error.isValidation ? Object.values(error.fieldErrors).flat().join(" ") : (error.problem?.detail ?? undefined) });
    },
    meta: { silent: true },
  });
  const toggle = useMutation({
    mutationFn: async (rule: SourceRule) =>
      unwrap(
        await client.PUT("/api/v1/suppliers/{supplierId}/source-rules/{ruleId}", {
          params: { path: { supplierId: supplier.id, ruleId: rule.id } },
          body: { ...ruleFromResponse(rule), enabled: !rule.enabled },
        }),
      ),
    onSuccess: (rule) => {
      toast.success(rule.enabled ? t("suppliers.routing.enabled") : t("suppliers.routing.disabled"));
      invalidate();
    },
  });
  const remove = useMutation({
    mutationFn: async (rule: SourceRule) => unwrap(await client.DELETE("/api/v1/suppliers/{supplierId}/source-rules/{ruleId}", { params: { path: { supplierId: supplier.id, ruleId: rule.id } } })),
    onSuccess: () => {
      toast.success(t("suppliers.routing.deleted"));
      invalidate();
    },
  });

  const set = <K extends keyof RuleDraft>(key: K, value: RuleDraft[K]) => setDraft((current) => (current ? { ...current, [key]: value } : current));

  return (
    <div className="flex flex-col gap-4" data-testid="supplier-routing">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <p className="max-w-prose text-sm text-muted-foreground">{t("suppliers.routing.description")}</p>
        {canEdit && !draft && (
          <Button size="sm" onClick={() => setDraft(EMPTY)} data-testid="source-rule-add">
            <Plus aria-hidden />
            {t("suppliers.routing.add")}
          </Button>
        )}
      </div>

      {rules.isPending ? (
        <Skeleton className="h-16" />
      ) : (rules.data ?? []).length === 0 ? (
        <EmptyState title={t("suppliers.routing.empty")} description={t("suppliers.routing.emptyDescription")} />
      ) : (
        <ul className="flex flex-col gap-2" aria-label={t("suppliers.routing.rules")}>
          {(rules.data ?? []).map((rule) => (
            <li key={rule.id} className="flex flex-wrap items-center gap-2 rounded-md border px-3 py-2 text-sm" data-testid="source-rule" data-enabled={rule.enabled}>
              <span className="min-w-0 flex-1 break-words">{rule.describe}</span>
              <Badge variant="outline">{t(`suppliers.routing.modes.${rule.mode}`, { defaultValue: rule.mode })}</Badge>
              {!rule.enabled && <Badge variant="warning">{rule.createdFromDocumentId ? t("suppliers.routing.proposal") : t("suppliers.routing.off")}</Badge>}
              {canEdit && (
                <>
                  <Switch checked={rule.enabled} onCheckedChange={() => toggle.mutate(rule)} disabled={toggle.isPending} aria-label={t("suppliers.routing.toggle")} />
                  <Button size="icon-xs" variant="ghost" aria-label={t("suppliers.routing.delete")} onClick={() => setDeleting(rule)}>
                    <Trash2 aria-hidden />
                  </Button>
                </>
              )}
            </li>
          ))}
        </ul>
      )}

      {draft && (
        <form
          className="grid gap-3 rounded-lg border p-3 sm:grid-cols-2"
          data-testid="source-rule-form"
          onSubmit={(event) => {
            event.preventDefault();
            create.mutate(draft);
          }}
        >
          <p className="text-xs text-muted-foreground sm:col-span-2">{t("suppliers.routing.formHint")}</p>
          {(["sender", "senderDomain", "recipient", "folderPrefix", "subjectRegex", "fileNameRegex"] as const).map((key) => (
            <div key={key} className="flex flex-col gap-1">
              <Label htmlFor={`rule-${key}`}>{t(`suppliers.routing.fields.${key}`)}</Label>
              <Input id={`rule-${key}`} value={draft[key]} onChange={(e) => set(key, e.target.value)} data-testid={`rule-${key}`} />
            </div>
          ))}
          <div className="flex flex-col gap-1">
            <Label htmlFor="rule-channel">{t("suppliers.routing.fields.channelId")}</Label>
            <Select value={draft.channelId} onValueChange={(v) => set("channelId", v)}>
              <SelectTrigger id="rule-channel" data-testid="rule-channel">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={NONE}>{t("suppliers.routing.anyChannel")}</SelectItem>
                {(channels.data ?? []).map((c) => (
                  <SelectItem key={c.id} value={c.id}>
                    {c.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <div className="flex flex-col gap-1">
            <Label htmlFor="rule-mode">{t("suppliers.routing.fields.mode")}</Label>
            <Select value={draft.mode} onValueChange={(v) => set("mode", v as RuleDraft["mode"])}>
              <SelectTrigger id="rule-mode">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="Exclusive">{t("suppliers.routing.modes.Exclusive")}</SelectItem>
                <SelectItem value="DefaultWhenNoContentMatch">{t("suppliers.routing.modes.DefaultWhenNoContentMatch")}</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <div className="flex flex-col gap-1">
            <Label htmlFor="rule-pin">{t("suppliers.routing.fields.pinTypeKey")}</Label>
            <Select value={draft.pinTypeKey} onValueChange={(v) => set("pinTypeKey", v)}>
              <SelectTrigger id="rule-pin" data-testid="rule-pin">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={NONE}>{t("suppliers.routing.noPin")}</SelectItem>
                {(types.data ?? []).filter((type) => type.publishedVersion != null).map((type) => (
                  <SelectItem key={type.key} value={type.key}>
                    {localized(type.name, i18n.language) ?? type.key}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <div className="flex flex-col gap-1">
            <Label htmlFor="rule-entity">{t("suppliers.routing.fields.receivingEntityId")}</Label>
            <Select value={draft.receivingEntityId} onValueChange={(v) => set("receivingEntityId", v)}>
              <SelectTrigger id="rule-entity">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value={NONE}>{t("suppliers.routing.noEntity")}</SelectItem>
                {(entities.data ?? []).map((e) => (
                  <SelectItem key={e.id} value={e.id}>
                    {e.code} · {e.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </div>
          <div className="flex flex-col gap-1">
            <Label htmlFor="rule-priority">{t("suppliers.routing.fields.priority")}</Label>
            <Input id="rule-priority" type="number" min={-1000} max={1000} value={draft.priority} onChange={(e) => set("priority", e.target.value)} className="w-28" />
          </div>
          <div className="flex items-center gap-2">
            <Checkbox id="rule-auth" checked={draft.requireSenderAuth} onCheckedChange={(c) => set("requireSenderAuth", c === true)} />
            <Label htmlFor="rule-auth" className="font-normal">
              {t("suppliers.routing.fields.requireSenderAuth")}
            </Label>
          </div>
          <div className="flex gap-2 sm:col-span-2">
            <Button type="submit" disabled={create.isPending} data-testid="source-rule-save">
              {create.isPending && <Loader2 className="animate-spin" aria-hidden />}
              {t("suppliers.routing.save")}
            </Button>
            <Button type="button" variant="outline" onClick={() => setDraft(null)}>
              {t("common:dialog.cancel")}
            </Button>
          </div>
        </form>
      )}

      <RoutingTestBox channels={channels.data ?? []} />

      <ConfirmDialog
        open={deleting !== null}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={t("suppliers.routing.deleteTitle")}
        description={deleting?.describe}
        confirmLabel={t("suppliers.routing.delete")}
        destructive
        onConfirm={() => (deleting ? remove.mutateAsync(deleting).then(() => setDeleting(null)) : undefined)}
      />
    </div>
  );
}

/** A stored rule as the PUT body (every condition kept). */
function ruleFromResponse(rule: SourceRule) {
  return {
    channelId: rule.channelId,
    recipient: rule.recipient,
    sender: rule.sender,
    senderDomain: rule.senderDomain,
    folderPrefix: rule.folderPrefix,
    subjectRegex: rule.subjectRegex,
    fileNameRegex: rule.fileNameRegex,
    mode: rule.mode,
    requireSenderAuth: rule.requireSenderAuth,
    priority: rule.priority,
    pinTypeKey: rule.pinTypeKey,
    receivingEntityId: rule.receivingEntityId,
    enabled: rule.enabled,
  };
}
