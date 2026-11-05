/** The window of Insights > Onboarding (docs/11 §17): the last `days` days up to today, as UTC dates (`GET /insights/automation?from=&to=`). */
export function insightsWindow(days: number, now: Date): { from: string; to: string } {
  const to = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()));
  const from = new Date(to.getTime() - (days - 1) * 86_400_000);
  const iso = (d: Date) => d.toISOString().slice(0, 10);
  return { from: iso(from), to: iso(to) };
}

/** A share as a percentage in the UI language ("42 %" / "42%"); a dash when there is nothing to divide. */
export function percent(part: number, whole: number, language: string): string {
  if (!whole) return "—";
  return new Intl.NumberFormat(language, { style: "percent", maximumFractionDigits: 0 }).format(part / whole);
}
