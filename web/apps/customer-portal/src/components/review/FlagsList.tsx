import { useTranslation } from "react-i18next";
import { AlertTriangle, Info, OctagonAlert } from "lucide-react";
import { Badge, Button, cn } from "@docproc/ui";
import type { DocumentFlag } from "@/lib/queries";
import type { ValidationState } from "@/lib/validation";

export interface FlagsListProps {
  flags: DocumentFlag[];
  /** Index of the flag selected with j/k. */
  selectedIndex: number;
  onSelect: (index: number) => void;
  /** Jump to the flag's field (focus the input, switch the page). */
  onJump: (flag: DocumentFlag) => void;
  fieldLabel: (path: string) => string;
  /** What an empty list means: only a validation run on the data on screen may claim that every rule passed. */
  validation: ValidationState;
}

const emptyKeys: Readonly<Record<ValidationState, string>> = {
  passed: "review.flags.none",
  notValidated: "review.flags.notValidated",
  stale: "review.flags.stale",
};

const severityOrder = { Blocking: 0, Warning: 1, Info: 2 } as const;

export function sortFlags(flags: DocumentFlag[]): DocumentFlag[] {
  return [...flags].sort((a, b) => (severityOrder[a.severity] ?? 3) - (severityOrder[b.severity] ?? 3) || a.code.localeCompare(b.code));
}

/** The flags strip at the top of the review screen; each entry jumps to its field. */
export function FlagsList({ flags, selectedIndex, onSelect, onJump, fieldLabel, validation }: FlagsListProps) {
  const { t } = useTranslation();
  if (flags.length === 0) {
    return (
      <p className="text-sm text-muted-foreground" data-testid="flags-empty" data-validation={validation}>
        {t(emptyKeys[validation])}
      </p>
    );
  }
  return (
    <ul className="flex flex-col gap-1" aria-label={t("review.flags.title", { count: flags.length })} data-testid="flags-list">
      {flags.map((flag, index) => {
        const Icon = flag.severity === "Blocking" ? OctagonAlert : flag.severity === "Warning" ? AlertTriangle : Info;
        const selected = index === selectedIndex;
        return (
          <li key={flag.id}>
            <Button
              type="button"
              variant="ghost"
              // Below sm the message gets a line of its own instead of wrapping one word per line beside the badges.
              className={cn("h-auto w-full flex-wrap justify-start gap-x-2 gap-y-1 px-2 py-1.5 text-left whitespace-normal sm:flex-nowrap", selected && "bg-accent")}
              aria-current={selected ? "true" : undefined}
              data-severity={flag.severity}
              data-flag-index={index}
              onClick={() => {
                onSelect(index);
                onJump(flag);
              }}
            >
              <Icon className={cn("size-4 shrink-0", flag.severity === "Blocking" ? "text-destructive" : flag.severity === "Warning" ? "text-amber-600" : "text-muted-foreground")} aria-hidden />
              <Badge variant={flag.severity === "Blocking" ? "destructive" : flag.severity === "Warning" ? "outline" : "secondary"} className="shrink-0">
                {t(`flagSeverity.${flag.severity}`)}
              </Badge>
              <span className="shrink-0 font-mono text-xs text-muted-foreground">{flag.code}</span>
              <span className="order-last min-w-0 basis-full sm:order-none sm:basis-0 sm:flex-1">{flag.message}</span>
              {flag.field && <span className="min-w-0 text-xs break-words sm:shrink-0 text-muted-foreground underline-offset-2 hover:underline">{fieldLabel(flag.field)}</span>}
              {flag.resolvedAt && <Badge variant="secondary">{t("review.flags.resolved")}</Badge>}
            </Button>
          </li>
        );
      })}
    </ul>
  );
}
