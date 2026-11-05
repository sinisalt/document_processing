import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { Check, Copy, KeyRound, Loader2, Save } from "lucide-react";
import { Alert, AlertDescription, AlertTitle, Badge, Button, Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle, Input, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue, Separator, Switch, Textarea } from "@docproc/ui";
import { ApiError, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import type { Destination } from "./api";
import { acknowledgementBody, acknowledgementDraft, callbackUrl, type AcknowledgementDraft, type CallbackAuth } from "./acknowledgements";

/**
 * How a destination reports back (docs/11 §13.8, §19 item 4 "callback setup"): a callback the receiver posts with an
 * HMAC signature or a bearer token (the platform generates the secret and shows it once; two slots for a rotation),
 * and/or a status poll of the destination's own origin, each with an optional JSONata mapping to `{documentId | externalId, status, message}`. A
 * rejection raises `EXTERNAL_REJECTED` on the settled document and opens an integration issue.
 */
export function AcknowledgementsDialog({ destination, open, onOpenChange }: { destination: Destination; open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState<AcknowledgementDraft>(() => acknowledgementDraft(destination.acknowledgements));
  // The generated secret, shown once in this dialog; never read back from the API.
  const [secret, setSecret] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const set = <K extends keyof AcknowledgementDraft>(key: K, value: AcknowledgementDraft[K]) => setDraft((current) => ({ ...current, [key]: value }));
  const invalidate = () => queryClient.invalidateQueries({ queryKey: queryKeys.outputs.destinations() });

  const save = useMutation({
    mutationFn: async () => unwrap(await client.PUT("/api/v1/destinations/{id}/acknowledgements", { params: { path: { id: destination.id } }, body: acknowledgementBody(draft) as never })),
    meta: { silent: true },
    onSuccess: async () => {
      setErrors({});
      toast.success(t("integrations.acknowledgements.saved", { name: destination.name }));
      await invalidate();
      onOpenChange(false);
    },
    onError: (error) => {
      if (error instanceof ApiError && error.isValidation) setErrors(error.fieldErrors);
      else toast.error(error instanceof ApiError ? error.title : String(error), { description: error instanceof ApiError ? (error.problem?.detail ?? undefined) : undefined });
    },
  });
  const rotateSecret = useMutation({
    mutationFn: async () => unwrap(await client.POST("/api/v1/destinations/{id}/callback-secret", { params: { path: { id: destination.id } } })),
    onSuccess: async (answer) => {
      setSecret(answer.secret);
      setCopied(false);
      toast.success(t("integrations.acknowledgements.secretSet"));
      await invalidate();
    },
  });
  const copy = async () => {
    if (!secret) return;
    try {
      await navigator.clipboard.writeText(secret);
      setCopied(true);
    } catch {
      toast.error(t("integrations.acknowledgements.copyFailed"));
    }
  };
  const error = (key: string) => errors[key]?.join(" ");

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-h-[90vh] overflow-y-auto sm:max-w-2xl" data-testid="acknowledgements-dialog">
        <DialogHeader>
          <DialogTitle>{t("integrations.acknowledgements.title", { name: destination.name })}</DialogTitle>
          <DialogDescription>{t("integrations.acknowledgements.description")}</DialogDescription>
        </DialogHeader>
        <div className="grid gap-4">
          <Label className="flex items-center gap-2 text-sm font-medium">
            <Switch checked={draft.callback} onCheckedChange={(on) => set("callback", on)} data-testid="ack-callback" />
            {t("integrations.acknowledgements.callback")}
          </Label>
          {draft.callback && (
            <div className="grid gap-3 border-l-2 pl-4">
              <div className="grid gap-1">
                <Label className="text-xs">{t("integrations.acknowledgements.callbackUrl")}</Label>
                <code className="rounded bg-muted px-2 py-1 font-mono text-xs break-all" data-testid="ack-callback-url">
                  {callbackUrl(window.location.origin, destination.id)}
                </code>
              </div>
              <div className="grid gap-1">
                <Label className="text-xs">{t("integrations.acknowledgements.auth")}</Label>
                <Select value={draft.auth} onValueChange={(value) => set("auth", value as CallbackAuth)}>
                  <SelectTrigger className="w-56" data-testid="ack-auth">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="hmacSha256">{t("integrations.acknowledgements.auths.hmacSha256")}</SelectItem>
                    <SelectItem value="bearer">{t("integrations.acknowledgements.auths.bearer")}</SelectItem>
                  </SelectContent>
                </Select>
                <p className="text-xs text-muted-foreground">{draft.auth === "hmacSha256" ? t("integrations.acknowledgements.hmacHint") : t("integrations.acknowledgements.bearerHint")}</p>
              </div>
              <div className="grid gap-1">
                <Label htmlFor="ack-callback-mapping" className="text-xs">
                  {t("integrations.acknowledgements.mapping")}
                </Label>
                <Textarea id="ack-callback-mapping" className="min-h-20 font-mono text-xs" spellCheck={false} value={draft.callbackMapping} placeholder='{ "externalId": id, "status": state = "ok" ? "Posted" : "Rejected", "message": reason }' onChange={(e) => set("callbackMapping", e.target.value)} />
                {error("callback.mapping") && <p className="text-xs text-destructive">{error("callback.mapping")}</p>}
              </div>
              <div className="grid gap-1">
                <Label className="flex items-center gap-2 text-xs">
                  {t("integrations.acknowledgements.secret")}
                  {destination.hasCallbackSecret ? <Badge variant="success">{t("integrations.acknowledgements.secretIsSet")}</Badge> : <Badge variant="warning">{t("integrations.acknowledgements.noSecret")}</Badge>}
                  {destination.hasPreviousCallbackSecret && <Badge variant="outline">{t("integrations.acknowledgements.previousSecret")}</Badge>}
                </Label>
                <div className="flex gap-2">
                  <Button type="button" variant="outline" disabled={rotateSecret.isPending} onClick={() => rotateSecret.mutate()} data-testid="ack-secret-set">
                    {rotateSecret.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <KeyRound aria-hidden />}
                    {destination.hasCallbackSecret || secret ? t("integrations.acknowledgements.rotate") : t("integrations.acknowledgements.setSecret")}
                  </Button>
                </div>
                {secret && (
                  <Alert data-testid="ack-secret-once">
                    <KeyRound aria-hidden />
                    <AlertTitle>{t("integrations.acknowledgements.oneTime")}</AlertTitle>
                    <AlertDescription className="grid gap-2">
                      <code className="break-all rounded bg-muted px-2 py-1 font-mono text-xs select-all" data-testid="ack-secret-value">
                        {secret}
                      </code>
                      <Button type="button" variant="outline" size="sm" className="justify-self-start" onClick={() => void copy()}>
                        {copied ? <Check aria-hidden /> : <Copy aria-hidden />}
                        {copied ? t("integrations.acknowledgements.copied") : t("integrations.acknowledgements.copy")}
                      </Button>
                    </AlertDescription>
                  </Alert>
                )}
                <p className="text-xs text-muted-foreground">{t("integrations.acknowledgements.secretHint")}</p>
              </div>
            </div>
          )}
          <Separator />
          <Label className="flex items-center gap-2 text-sm font-medium">
            <Switch checked={draft.poll} onCheckedChange={(on) => set("poll", on)} data-testid="ack-poll" />
            {t("integrations.acknowledgements.poll")}
          </Label>
          {draft.poll && (
            <div className="grid gap-3 border-l-2 pl-4">
              <div className="grid gap-1">
                <Label htmlFor="ack-url" className="text-xs">
                  {t("integrations.acknowledgements.urlTemplate")}
                </Label>
                <Input id="ack-url" className="font-mono text-xs" value={draft.urlTemplate} placeholder="https://erp.example/api/ap/invoices/{externalId}/status" onChange={(e) => set("urlTemplate", e.target.value)} data-testid="ack-url" />
                <p className="text-xs text-muted-foreground">{t("integrations.acknowledgements.urlHint")}</p>
                {error("statusPoll.urlTemplate") && <p className="text-xs text-destructive">{error("statusPoll.urlTemplate")}</p>}
              </div>
              <div className="flex flex-wrap gap-3">
                <div className="grid gap-1">
                  <Label htmlFor="ack-interval" className="text-xs">
                    {t("integrations.acknowledgements.interval")}
                  </Label>
                  <Input id="ack-interval" className="w-28" inputMode="numeric" value={draft.intervalMinutes} onChange={(e) => set("intervalMinutes", e.target.value)} />
                  {error("statusPoll.intervalMinutes") && <p className="text-xs text-destructive">{error("statusPoll.intervalMinutes")}</p>}
                </div>
                <div className="grid gap-1">
                  <Label htmlFor="ack-max" className="text-xs">
                    {t("integrations.acknowledgements.maxPolls")}
                  </Label>
                  <Input id="ack-max" className="w-28" inputMode="numeric" value={draft.maxPolls} placeholder="288" onChange={(e) => set("maxPolls", e.target.value)} />
                  {error("statusPoll.maxPolls") && <p className="text-xs text-destructive">{error("statusPoll.maxPolls")}</p>}
                </div>
              </div>
              <div className="grid gap-1">
                <Label htmlFor="ack-poll-mapping" className="text-xs">
                  {t("integrations.acknowledgements.mapping")}
                </Label>
                <Textarea id="ack-poll-mapping" className="min-h-20 font-mono text-xs" spellCheck={false} value={draft.pollMapping} onChange={(e) => set("pollMapping", e.target.value)} />
              </div>
            </div>
          )}
          {Object.entries(errors)
            .filter(([key]) => !key.startsWith("callback.") && !key.startsWith("statusPoll."))
            .map(([key, messages]) => (
              <p key={key} className="text-sm text-destructive" role="alert">
                {key}: {messages.join(" ")}
              </p>
            ))}
        </div>
        <DialogFooter>
          <Button type="button" variant="outline" onClick={() => onOpenChange(false)}>
            {t("integrations.back")}
          </Button>
          <Button type="button" onClick={() => save.mutate()} disabled={save.isPending} aria-busy={save.isPending} data-testid="ack-save">
            <Save aria-hidden />
            {t("integrations.save")}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
