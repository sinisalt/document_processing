import { useEffect, useRef } from "react";
import { useTranslation } from "react-i18next";
import { ArrowRight, Check, Loader2, ScanText, SkipForward, X } from "lucide-react";
import { Button, Input, Label } from "@docproc/ui";
import { REVIEW_NS } from "../lib/labels";

export interface MapPreviewProps {
  /** The focused field's label, or null when no field is focused yet. */
  fieldLabel: string | null;
  /** The text read under the selection ("28.10.2026"), null while loading or when there is no selection. */
  text: string | null;
  /** The value that will go into the field, as the form shows it ("2026-10-28"); editable before it is assigned. */
  value: string;
  onValueChange: (value: string) => void;
  /** Whether the reading fits the field's kind; otherwise the text itself is offered. */
  fits: boolean;
  loading?: boolean;
  /** No words under the selection: offer region OCR (`regions:read`). */
  noWords?: boolean;
  ocrPending?: boolean;
  /** Why the OCR could not run (no OCR configured on this server, for one). */
  error?: string | null;
  onAssign: () => void;
  onSkip: () => void;
  onCancel: () => void;
  onOcr?: () => void;
}

/**
 * The preview of map mode (docs/11 §12.3): "Valid from ← 01.01.2027 → 2027-01-01", editable before it is accepted.
 * Enter assigns and moves to the next empty required field, Tab skips to it, Esc cancels.
 */
export function MapPreview(props: MapPreviewProps) {
  const { t } = useTranslation(REVIEW_NS);
  const input = useRef<HTMLInputElement>(null);
  const hasSelection = props.text !== null || !!props.loading || !!props.noWords;
  useEffect(() => {
    if (props.text !== null) input.current?.focus({ preventScroll: true });
  }, [props.text]);

  if (props.fieldLabel === null) {
    return (
      <p className="text-sm text-muted-foreground" data-testid="map-preview" data-state="no-field">
        {t("map.focusHint")}
      </p>
    );
  }
  if (!hasSelection) {
    return (
      <p className="text-sm" data-testid="map-preview" data-state="waiting">
        {t("map.pickHint", { field: props.fieldLabel })}
      </p>
    );
  }
  return (
    <div
      className="flex flex-wrap items-end gap-2"
      data-testid="map-preview"
      data-state={props.loading ? "loading" : props.noWords ? "no-words" : "ready"}
      onKeyDown={(event) => {
        if (event.key === "Enter") {
          event.preventDefault();
          if (props.text !== null) props.onAssign();
        } else if (event.key === "Escape") {
          event.preventDefault();
          event.stopPropagation();
          props.onCancel();
        } else if (event.key === "Tab" && !event.shiftKey) {
          event.preventDefault();
          props.onSkip();
        }
      }}
    >
      <div className="flex min-w-0 flex-col gap-1">
        <span className="text-xs text-muted-foreground">{props.fieldLabel}</span>
        <span className="flex items-center gap-1 text-sm">
          {props.loading ? <Loader2 className="size-4 animate-spin" aria-label={t("map.reading")} /> : <q className="font-mono" data-testid="map-text">{props.text ?? ""}</q>}
          <ArrowRight className="size-3.5 text-muted-foreground" aria-hidden />
        </span>
      </div>
      {props.noWords ? (
        <div className="flex flex-col gap-1">
          <p className="text-sm">{t("map.noWords")}</p>
          {props.onOcr && (
            <Button type="button" size="sm" variant="outline" onClick={props.onOcr} disabled={props.ocrPending}>
              {props.ocrPending ? <Loader2 className="animate-spin" aria-hidden /> : <ScanText aria-hidden />}
              {t("map.ocr")}
            </Button>
          )}
        </div>
      ) : (
        <div className="flex flex-col gap-1">
          <Label htmlFor="map-value" className="text-xs text-muted-foreground">
            {props.fits ? t("map.value") : t("map.valueAsText")}
          </Label>
          <Input id="map-value" ref={input} value={props.value} onChange={(event) => props.onValueChange(event.target.value)} className="h-8 w-48" disabled={props.loading} data-testid="map-value" />
        </div>
      )}
      <div className="flex gap-1">
        <Button type="button" size="sm" onClick={props.onAssign} disabled={props.loading || props.text === null || !!props.noWords} data-testid="map-assign" aria-keyshortcuts="Enter">
          <Check aria-hidden />
          {t("map.assign")}
        </Button>
        <Button type="button" size="sm" variant="ghost" onClick={props.onSkip} aria-keyshortcuts="Tab">
          <SkipForward aria-hidden />
          {t("map.skip")}
        </Button>
        <Button type="button" size="sm" variant="ghost" onClick={props.onCancel} aria-keyshortcuts="Escape" aria-label={t("map.cancel")}>
          <X aria-hidden />
        </Button>
      </div>
      {props.error && (
        <p className="basis-full text-sm text-destructive" role="alert">
          {props.error}
        </p>
      )}
    </div>
  );
}
