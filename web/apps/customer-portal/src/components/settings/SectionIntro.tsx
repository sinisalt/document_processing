import type { ReactNode } from "react";

/**
 * The heading of a settings tab that is a list (channels, members, API keys): what the list is and what it is for,
 * with the tab's action beside it, so every tab opens with a title and an explanation like the card-based ones.
 */
export function SectionIntro({ title, description, action }: { title: string; description: string; action?: ReactNode }) {
  return (
    <div className="flex flex-wrap items-start justify-between gap-4">
      <div className="max-w-2xl space-y-1">
        <h2 className="text-base leading-none font-semibold">{title}</h2>
        <p className="text-sm text-muted-foreground">{description}</p>
      </div>
      {action}
    </div>
  );
}
