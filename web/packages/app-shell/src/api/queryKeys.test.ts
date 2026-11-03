import { QueryClient, hashKey } from "@tanstack/react-query";
import { queryKeys } from "./queryKeys";

describe("queryKeys", () => {
  it("keeps limit in a plain list key and drops only the cursor", () => {
    expect(queryKeys.tenants.list({ limit: 200 })).toEqual(["tenants", "list", { limit: 200 }]);
    expect(queryKeys.documents.list({ status: "Failed", limit: 25, cursor: "abc" })).toEqual(["documents", "list", { status: "Failed", limit: 25 }]);
    expect(hashKey(queryKeys.documents.list({ status: "Failed", limit: 25 }))).not.toBe(hashKey(queryKeys.documents.list({ status: "Failed", limit: 200 })));
  });

  it("gives infinite lists their own segment without cursor and limit", () => {
    expect(queryKeys.documents.pages({ status: "Failed", limit: 50, cursor: "abc" })).toEqual(["documents", "pages", { status: "Failed" }]);
    expect(queryKeys.reviewTasks.pages({ status: "Open", limit: 50 })).toEqual(["review-tasks", "pages", { status: "Open" }]);
    expect(queryKeys.suppliers.pages()).toEqual(["suppliers", "pages", {}]);
  });

  it("never lets a plain lookup and an infinite list with the same filters share an entry", () => {
    for (const [plain, pages] of [
      [queryKeys.tenants.list({ limit: 200 }), queryKeys.tenants.pages({})],
      [queryKeys.documents.list({ status: "NeedsReview", limit: 200 }), queryKeys.documents.pages({ status: "NeedsReview", limit: 200 })],
      [queryKeys.reviewTasks.list({ limit: 200 }), queryKeys.reviewTasks.pages({ limit: 200 })],
      [queryKeys.suppliers.list({ limit: 200 }), queryKeys.suppliers.pages({})],
      [queryKeys.ingestions.list({}), queryKeys.ingestions.pages({})],
    ] as const) {
      expect(hashKey(plain)).not.toBe(hashKey(pages));
    }
  });

  it("invalidating a resource covers both shapes, but not the page images", async () => {
    const client = new QueryClient();
    client.setQueryData(queryKeys.documents.list({ limit: 25 }), 1);
    client.setQueryData(queryKeys.documents.pages({}), 2);
    client.setQueryData(queryKeys.documents.detail("d1"), 3);
    client.setQueryData(queryKeys.documentPages.page("d1", 1), 4);

    await client.invalidateQueries({ queryKey: queryKeys.documents.all });

    expect(client.getQueryState(queryKeys.documents.list({ limit: 25 }))?.isInvalidated).toBe(true);
    expect(client.getQueryState(queryKeys.documents.pages({}))?.isInvalidated).toBe(true);
    expect(client.getQueryState(queryKeys.documents.detail("d1"))?.isInvalidated).toBe(true);
    expect(client.getQueryState(queryKeys.documentPages.page("d1", 1))?.isInvalidated).toBe(false);
  });

  it("keeps the type catalogue under one prefix, a version under its type", async () => {
    expect(queryKeys.documentTypes.version("price-list", 2)).toEqual(["document-types", "detail", "price-list", "version", 2]);
    const client = new QueryClient();
    client.setQueryData(queryKeys.documentTypes.list(), 1);
    client.setQueryData(queryKeys.documentTypes.templates(), 2);
    client.setQueryData(queryKeys.documentTypes.version("price-list", 2), 3);
    client.setQueryData(queryKeys.documentTypes.version("Invoice", 1), 4);

    await client.invalidateQueries({ queryKey: queryKeys.documentTypes.detail("price-list") });
    expect(client.getQueryState(queryKeys.documentTypes.version("price-list", 2))?.isInvalidated).toBe(true);
    expect(client.getQueryState(queryKeys.documentTypes.version("Invoice", 1))?.isInvalidated).toBe(false);

    await client.invalidateQueries({ queryKey: queryKeys.documentTypes.all });
    expect(client.getQueryState(queryKeys.documentTypes.list())?.isInvalidated).toBe(true);
    expect(client.getQueryState(queryKeys.documentTypes.templates())?.isInvalidated).toBe(true);
  });

  it("keeps a mapping's versions under the mapping and deliveries apart from documents", async () => {
    expect(queryKeys.outputs.version("article-price-import", 1)).toEqual(["outputs", "mappings", "article-price-import", "version", 1]);
    expect(queryKeys.deliveries.list({ documentId: "d1", limit: 50, cursor: "c" })).toEqual(["deliveries", "list", { documentId: "d1", limit: 50 }]);
    const client = new QueryClient();
    client.setQueryData(queryKeys.outputs.version("article-price-import", 1), 1);
    client.setQueryData(queryKeys.outputs.version("erp-ap-import", 1), 2);
    client.setQueryData(queryKeys.outputs.destinations(), 3);
    client.setQueryData(queryKeys.deliveries.detail("x"), 4);

    await client.invalidateQueries({ queryKey: queryKeys.outputs.mapping("article-price-import") });
    expect(client.getQueryState(queryKeys.outputs.version("article-price-import", 1))?.isInvalidated).toBe(true);
    expect(client.getQueryState(queryKeys.outputs.version("erp-ap-import", 1))?.isInvalidated).toBe(false);

    await client.invalidateQueries({ queryKey: queryKeys.documents.all });
    expect(client.getQueryState(queryKeys.deliveries.detail("x"))?.isInvalidated).toBe(false);
    await client.invalidateQueries({ queryKey: queryKeys.outputs.all });
    expect(client.getQueryState(queryKeys.outputs.destinations())?.isInvalidated).toBe(true);
  });
});
