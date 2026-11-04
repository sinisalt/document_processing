import { useState } from "react";
import { useTranslation } from "react-i18next";
import { useMutation } from "@tanstack/react-query";
import { FlaskConical, Loader2 } from "lucide-react";
import { Badge, Button, Input, Label, Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@docproc/ui";
import { unwrap, useApi } from "@docproc/app-shell";
import type { Channel } from "@/lib/queries";
import { splitLines, type RoutingTestRequest, type RoutingTestResponse } from "@/lib/routingQueries";

const NONE = "__none__";
const AUTH_RESULTS = ["pass", "fail", "none"] as const;

/** The test box's fields as `POST /routing:test`'s body: what is empty is not sent; DMARC "none" sends no authentication. */
export function routingTestBody(values: { sender: string; recipients: string; channelId: string; folderPath: string; subject: string; fileName: string; dmarc: string; spf: string }): RoutingTestRequest {
  const text = (value: string) => (value.trim() === "" ? null : value.trim());
  const recipients = splitLines(values.recipients);
  const auth = values.dmarc !== "none" || values.spf !== "none" ? { dmarc: values.dmarc === "none" ? null : values.dmarc, spf: values.spf === "none" ? null : values.spf } : null;
  return {
    sender: text(values.sender),
    recipients: recipients.length > 0 ? recipients : null,
    channelId: values.channelId && values.channelId !== NONE ? values.channelId : null,
    folderPath: text(values.folderPath),
    subject: text(values.subject),
    fileName: text(values.fileName),
    senderAuth: auth,
  };
}

/**
 * "Test routing" (docs/11 §19, Suppliers sheet Routing tab and Settings > Routing): a source as ingest would see it
 * (sender with its DMARC and SPF results, recipients, channel, folder, subject, file name) run against the tenant's
 * enabled source rules by `POST /routing:test`: the supplier it would get and why, the flags, the pinned type, and every
 * candidate rule with the conditions that held and failed.
 */
export function RoutingTestBox({ channels, initialSender = "" }: { channels: readonly Channel[]; initialSender?: string }) {
  const { t } = useTranslation();
  const { client } = useApi();
  const [values, setValues] = useState({ sender: initialSender, recipients: "", channelId: NONE, folderPath: "", subject: "", fileName: "", dmarc: "pass", spf: "none" });
  const set = (key: keyof typeof values) => (value: string) => setValues((current) => ({ ...current, [key]: value }));
  const test = useMutation({ mutationFn: async () => unwrap(await client.POST("/api/v1/routing:test", { body: routingTestBody(values) })) });
  const result: RoutingTestResponse | undefined = test.data;

  return (
    <section className="flex flex-col gap-3 rounded-lg border p-4" aria-labelledby="routing-test-heading" data-testid="routing-test">
      <h3 id="routing-test-heading" className="flex items-center gap-2 text-sm font-semibold">
        <FlaskConical className="size-4" aria-hidden />
        {t("routing.test.title")}
      </h3>
      <p className="text-sm text-muted-foreground">{t("routing.test.description")}</p>
      <form
        className="grid gap-3 sm:grid-cols-2"
        onSubmit={(event) => {
          event.preventDefault();
          test.mutate();
        }}
      >
        <div className="flex flex-col gap-1">
          <Label htmlFor="routing-test-sender">{t("routing.test.sender")}</Label>
          <Input id="routing-test-sender" value={values.sender} onChange={(e) => set("sender")(e.target.value)} placeholder="rechnungsversand@nordwind.example" data-testid="routing-test-sender" />
        </div>
        <div className="flex flex-col gap-1">
          <Label htmlFor="routing-test-recipients">{t("routing.test.recipients")}</Label>
          <Input id="routing-test-recipients" value={values.recipients} onChange={(e) => set("recipients")(e.target.value)} placeholder="invoices@tenant-a.local" />
        </div>
        <div className="flex flex-col gap-1">
          <Label htmlFor="routing-test-dmarc">{t("routing.test.dmarc")}</Label>
          <Select value={values.dmarc} onValueChange={set("dmarc")}>
            <SelectTrigger id="routing-test-dmarc" data-testid="routing-test-dmarc">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {AUTH_RESULTS.map((r) => (
                <SelectItem key={r} value={r}>
                  {t(`routing.test.auth.${r}`)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <div className="flex flex-col gap-1">
          <Label htmlFor="routing-test-spf">{t("routing.test.spf")}</Label>
          <Select value={values.spf} onValueChange={set("spf")}>
            <SelectTrigger id="routing-test-spf">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {AUTH_RESULTS.map((r) => (
                <SelectItem key={r} value={r}>
                  {t(`routing.test.auth.${r}`)}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <div className="flex flex-col gap-1">
          <Label htmlFor="routing-test-channel">{t("routing.test.channel")}</Label>
          <Select value={values.channelId} onValueChange={set("channelId")}>
            <SelectTrigger id="routing-test-channel">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value={NONE}>{t("routing.test.anyChannel")}</SelectItem>
              {channels.map((c) => (
                <SelectItem key={c.id} value={c.id}>
                  {c.name}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <div className="flex flex-col gap-1">
          <Label htmlFor="routing-test-folder">{t("routing.test.folderPath")}</Label>
          <Input id="routing-test-folder" value={values.folderPath} onChange={(e) => set("folderPath")(e.target.value)} placeholder="lieferanten/nordwind/" />
        </div>
        <div className="flex flex-col gap-1">
          <Label htmlFor="routing-test-subject">{t("routing.test.subject")}</Label>
          <Input id="routing-test-subject" value={values.subject} onChange={(e) => set("subject")(e.target.value)} />
        </div>
        <div className="flex flex-col gap-1">
          <Label htmlFor="routing-test-file">{t("routing.test.fileName")}</Label>
          <Input id="routing-test-file" value={values.fileName} onChange={(e) => set("fileName")(e.target.value)} />
        </div>
        <div className="sm:col-span-2">
          <Button type="submit" variant="secondary" disabled={test.isPending} data-testid="routing-test-submit">
            {test.isPending ? <Loader2 className="animate-spin" aria-hidden /> : <FlaskConical aria-hidden />}
            {t("routing.test.run")}
          </Button>
        </div>
      </form>
      {result && (
        <div className="flex flex-col gap-2 rounded-md bg-muted/40 p-3 text-sm" data-testid="routing-test-result" aria-live="polite">
          <div className="flex flex-wrap items-center gap-2">
            <span className="font-medium" data-testid="routing-test-winner">
              {result.supplierName ? t("routing.test.winner", { supplier: result.supplierName }) : t("routing.test.noWinner")}
            </span>
            {result.method && <Badge variant="outline">{t(`routing.test.method.${result.method}`, { defaultValue: result.method })}</Badge>}
            {result.mode && <Badge variant="outline">{result.mode}</Badge>}
            {result.confidence != null && <Badge variant="secondary">{Math.round(Number(result.confidence) * 100)} %</Badge>}
            {result.pinTypeKey && <Badge variant="secondary">{t("routing.test.pins", { type: result.pinTypeKey })}</Badge>}
            {result.flags.map((flag) => (
              <Badge key={flag} variant="warning">
                {t(`flagCodes.${flag}`, { defaultValue: flag })}
              </Badge>
            ))}
          </div>
          <p className="text-muted-foreground" data-testid="routing-test-explanation">
            {result.explanation}
          </p>
          {result.candidates.length > 0 && (
            <ul className="flex flex-col gap-1" aria-label={t("routing.test.candidates")}>
              {result.candidates.map((candidate) => (
                <li key={candidate.ruleId} className="rounded border bg-background px-2 py-1" data-testid="routing-test-candidate" data-matched={candidate.matched}>
                  <span className={candidate.matched ? "font-medium" : "text-muted-foreground"}>{candidate.describe}</span>
                  <span className="ml-2 text-xs text-muted-foreground">
                    {candidate.held.length > 0 && t("routing.test.held", { conditions: candidate.held.join(", ") })}
                    {candidate.failed.length > 0 && ` ${t("routing.test.failed", { conditions: candidate.failed.join(", ") })}`}
                    {candidate.unverified && ` ${t("routing.test.unverified")}`}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}
    </section>
  );
}
