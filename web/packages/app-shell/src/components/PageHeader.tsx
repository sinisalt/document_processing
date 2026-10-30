import type { ReactNode } from "react";
import { cn } from "@docproc/ui";

export interface PageHeaderProps {
  title: ReactNode;
  description?: ReactNode;
  /** Buttons on the right. */
  actions?: ReactNode;
  className?: string;
}

/** The first thing on every screen: an h1, an optional line under it, actions on the right. */
export function PageHeader({ title, description, actions, className }: PageHeaderProps) {
  return (
    <div className={cn("flex flex-wrap items-start justify-between gap-4", className)}>
      <div className="min-w-0 space-y-1">
        <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
        {description && <p className="text-sm text-muted-foreground">{description}</p>}
      </div>
      {actions && <div className="flex shrink-0 items-center gap-2">{actions}</div>}
    </div>
  );
}
