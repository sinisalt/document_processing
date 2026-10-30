import { useTranslation } from "react-i18next";
import { Loader2 } from "lucide-react";
import { cn } from "@docproc/ui";

export function LoadingScreen({ label, className }: { label?: string; className?: string }) {
  const { t } = useTranslation("common");
  return (
    <div role="status" aria-live="polite" className={cn("flex min-h-[50vh] flex-col items-center justify-center gap-3 text-muted-foreground", className)}>
      <Loader2 className="size-6 animate-spin" aria-hidden />
      <span className="text-sm">{label ?? t("app.loading")}</span>
    </div>
  );
}
