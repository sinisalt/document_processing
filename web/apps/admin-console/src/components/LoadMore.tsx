import { useTranslation } from "react-i18next";
import { Loader2 } from "lucide-react";
import { Button } from "@docproc/ui";
import type { UseCursorListResult } from "@docproc/app-shell";

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
