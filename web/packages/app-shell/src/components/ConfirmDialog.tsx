import { useState, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Loader2 } from "lucide-react";
import {
  AlertDialog,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
  AlertDialogTrigger,
  Button,
} from "@docproc/ui";

export interface ConfirmDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: ReactNode;
  description?: ReactNode;
  confirmLabel?: ReactNode;
  cancelLabel?: ReactNode;
  /** Red confirm button for approve/reject/delete style actions that cannot be undone. */
  destructive?: boolean;
  /** Awaited; the dialog closes when it resolves and stays open (with the error toasted by the caller) when it rejects. */
  onConfirm: () => void | Promise<void>;
  /** Pending state driven from outside (a mutation's `isPending`); the dialog also tracks its own await. */
  isPending?: boolean;
  /** Optional element that opens the dialog (otherwise control `open` yourself). */
  trigger?: ReactNode;
  /** Extra content between the description and the buttons, e.g. a reason textarea. */
  children?: ReactNode;
  confirmDisabled?: boolean;
}

export function ConfirmDialog({
  open,
  onOpenChange,
  title,
  description,
  confirmLabel,
  cancelLabel,
  destructive = false,
  onConfirm,
  isPending = false,
  trigger,
  children,
  confirmDisabled = false,
}: ConfirmDialogProps) {
  const { t } = useTranslation("common");
  const [running, setRunning] = useState(false);
  const busy = running || isPending;

  const confirm = async () => {
    setRunning(true);
    try {
      await onConfirm();
      onOpenChange(false);
    } catch {
      // The caller's mutation reports the problem (global toast or inline); keep the dialog open to retry.
    } finally {
      setRunning(false);
    }
  };

  return (
    <AlertDialog open={open} onOpenChange={(next) => (busy && !next ? undefined : onOpenChange(next))}>
      {trigger && <AlertDialogTrigger asChild>{trigger}</AlertDialogTrigger>}
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>{title}</AlertDialogTitle>
          {description && <AlertDialogDescription>{description}</AlertDialogDescription>}
        </AlertDialogHeader>
        {children}
        <AlertDialogFooter>
          <AlertDialogCancel disabled={busy}>{cancelLabel ?? t("dialog.cancel")}</AlertDialogCancel>
          <Button type="button" variant={destructive ? "destructive" : "default"} onClick={() => void confirm()} disabled={busy || confirmDisabled} aria-busy={busy}>
            {busy && <Loader2 className="animate-spin" aria-hidden />}
            {busy ? t("dialog.working") : confirmLabel ?? t("dialog.confirm")}
          </Button>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  );
}
