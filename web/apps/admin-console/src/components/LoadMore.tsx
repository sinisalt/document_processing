import { useTranslation } from "react-i18next";
import { Loader2 } from "lucide-react";
import { Button } from "@docproc/ui";
import type { UseCursorListResult } from "@docproc/app-shell";

/** The row count under a grid that loads in one page (members, channels, API keys), worded like {@link LoadMore}'s. */
export function RowCount({ count }: { count: number | undefined }) {
  const { t } = useTranslation("common");
  // Nothing for an empty grid: its empty state already says so, "0 rows" under it only repeats it.
  if (!count) return null;
  return <span aria-live="polite">{t("table.rowCount", { count })}</span>;
}

/** The footer of every cursor-paginated grid: how many rows are loaded and the way to fetch the next page. */
export function LoadMore<T>({ list }: { list: UseCursorListResult<T> }) {
  const { t } = useTranslation("common");
  return (
    <>
      <span aria-live="polite">{t("table.rowCount", { count: list.items.length })}</span>
      {list.hasNextPage && (
        <Button variant="outline" size="sm" onClick={() => void list.fetchNextPage()} disabled={list.isFetchingNextPage} aria-busy={list.isFetchingNextPage}>
          {list.isFetchingNextPage && <Loader2 className="animate-spin" aria-hidden />}
          {t("table.loadMore")}
        </Button>
      )}
    </>
  );
}
