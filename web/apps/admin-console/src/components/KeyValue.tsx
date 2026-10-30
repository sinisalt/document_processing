import type { ReactNode } from "react";
import { cn } from "@docproc/ui";

export interface KeyValueItem {
  label: ReactNode;
  value: ReactNode;
  /** Monospace for ids, slugs and codes. */
  mono?: boolean;
}

/** A definition list of label/value pairs: tenant identity, document summary, costs. */
export function KeyValue({ items, columns = 2, className }: { items: KeyValueItem[]; columns?: 1 | 2 | 3; className?: string }) {
  return (
    <dl className={cn("grid gap-x-6 gap-y-3 text-sm", columns === 1 ? "grid-cols-1" : columns === 2 ? "sm:grid-cols-2" : "sm:grid-cols-2 lg:grid-cols-3", className)}>
      {items.map((item, index) => (
        <div key={index} className="min-w-0">
          <dt className="text-xs font-medium uppercase tracking-wide text-muted-foreground">{item.label}</dt>
          <dd className={cn("mt-0.5 break-words", item.mono && "font-mono text-xs")}>{item.value ?? "—"}</dd>
        </div>
      ))}
    </dl>
  );
}

/** Raw JSON in a scrollable block (channel config, event details). */
export function JsonBlock({ value, className }: { value: unknown; className?: string }) {
  let text: string;
  try {
    text = JSON.stringify(value, null, 2) ?? "null";
  } catch {
    text = String(value);
  }
  return <pre className={cn("max-h-80 overflow-auto rounded-md bg-muted p-3 font-mono text-xs", className)}>{text}</pre>;
}
