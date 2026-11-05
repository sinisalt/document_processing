import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { toast } from "sonner";
import { CheckCircle2, Globe, Loader2 } from "lucide-react";
import { Alert, AlertDescription, AlertTitle, Button, Input, Label } from "@docproc/ui";
import { ConfirmDialog, isApiError, queryKeys, unwrap, useApi } from "@docproc/app-shell";
import { TestSummary, otherTypesTest, type TenantRule, type TenantRuleTest } from "@docproc/review";

export interface TenantRuleActionProps {
  documentId: string;
  /** The document's type, which the rule gives (the document must be of a type of the catalogue). */
  typeKey: string | null;
  /** The rule the chips make (`ruleOf(draft)`); null while no positive identifier is marked. */
  rule: Record<string, unknown> | null;
  /** The rule holds on this document (the live test of the card); the API refuses one that does not. */
  holds: boolean;
}

/**
 * "Use for all suppliers without own layouts" (docs/11 §12.10 step 5, §19 item 1): a ConfigAdmin turns the identifiers
 * marked on this document into a tenant fallback rule of its type (`POST /documents/{id}/identification-rules`). The
 * rule is saved as a Draft with its save-time test over the last 500 approved documents ("would have matched N
 * documents of this type, M others"); Activate makes it decide, confirming when the test matched other types.
 */
export function TenantRuleAction({ documentId, typeKey, rule, holds }: TenantRuleActionProps) {
  const { t } = useTranslation();
  const { client } = useApi();
  const queryClient = useQueryClient();
  const [name, setName] = useState("");
  const [saved, setSaved] = useState<TenantRule | null>(null);
  const [confirm, setConfirm] = useState<TenantRuleTest | null>(null);
  const draft = saved?.versions.find((v) => v.state === "Draft") ?? null;

  const create = useMutation({
    mutationFn: async () =>
      unwrap(await client.POST("/api/v1/documents/{id}/identification-rules", { params: { path: { id: documentId } }, body: { rule: rule as never, typeKey, name: name.trim() || null } })),
    onSuccess: async (created) => {
      setSaved(created);
      toast.success(t("review.tenantRule.saved", { name: created.name }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.identificationRules.all });
    },
  });
  const activate = useMutation({
    mutationFn: async (confirmOtherTypes: boolean) =>
      unwrap(await client.POST("/api/v1/identification-rules/{id}/versions/{version}:activate", { params: { path: { id: saved!.id, version: Number(draft!.version) } }, body: { confirmOtherTypes } })),
    meta: { silent: true },
    onSuccess: async (active) => {
      setSaved(active);
      setConfirm(null);
      toast.success(t("review.tenantRule.activated", { name: active.name }));
      await queryClient.invalidateQueries({ queryKey: queryKeys.identificationRules.all });
    },
    onError: (error) => {
      const other = isApiError(error) && error.isConflict ? otherTypesTest(error.problem) : null;
      if (other) setConfirm(other);
      else toast.error(isApiError(error) ? error.title : String(error), { description: isApiError(error) ? (error.problem?.detail ?? undefined) : undefined });
    },
  });

  if (!typeKey) return null;
  return (
    <div className="flex flex-col gap-2 rounded-md border p-3" data-testid="tenant-rule-action">
      <h3 className="flex items-center gap-1.5 text-sm font-semibold">
        <Globe className="size-4" aria-hidden />
        {t("review.tenantRule.title")}
      </h3>
      <p className="text-xs text-muted-foreground">{t("review.tenantRule.description", { type: typeKey })}</p>
      {!saved ? (
        <>
          <div className="grid gap-1">
            <Label htmlFor="tenant-rule-name" className="text-xs">
              {t("review.tenantRule.name")}
            </Label>
            <Input id="tenant-rule-name" value={name} maxLength={200} placeholder={t("review.tenantRule.namePlaceholder")} onChange={(event) => setName(event.target.value)} />
          </div>
          <div>
            <Button type="button" size="sm" variant="secondary" disabled={!rule || !holds || create.isPending} onClick={() => create.mutate()} data-testid="use-for-all-suppliers" title={!holds ? t("review.tenantRule.mustHold") : undefined}>
              {create.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <Globe aria-hidden />}
              {t("review.tenantRule.useForAll")}
            </Button>
          </div>
        </>
      ) : (
        <Alert data-testid="tenant-rule-saved" data-state={saved.status === "Active" && saved.activeVersion !== null ? "Active" : "Draft"}>
          <AlertTitle>{saved.activeVersion !== null ? t("review.tenantRule.activeTitle", { name: saved.name }) : t("review.tenantRule.draftTitle", { name: saved.name })}</AlertTitle>
          <AlertDescription className="flex flex-col gap-2">
            <TestSummary test={(draft ?? saved.versions[0])?.test} />
            {draft && (
              <div className="flex flex-wrap gap-2">
                <Button type="button" size="sm" onClick={() => activate.mutate(false)} disabled={activate.isPending} data-testid="tenant-rule-activate">
                  {activate.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <CheckCircle2 aria-hidden />}
                  {t("review.tenantRule.activate")}
                </Button>
                <a className="self-center text-xs underline underline-offset-2" href={`/settings?tab=types&type=${encodeURIComponent(typeKey)}`}>
                  {t("review.tenantRule.openEditor")}
                </a>
              </div>
            )}
          </AlertDescription>
        </Alert>
      )}
      <ConfirmDialog
        open={confirm !== null}
        onOpenChange={(open) => !open && setConfirm(null)}
        title={t("review.tenantRule.confirmTitle")}
        description={t("review.tenantRule.confirm", { summary: confirm?.summary ?? "" })}
        confirmLabel={t("review.tenantRule.activateAnyway")}
        destructive
        isPending={activate.isPending}
        onConfirm={() => activate.mutateAsync(true).then(() => undefined)}
      />
    </div>
  );
}
