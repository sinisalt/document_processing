import { test, expect } from "@playwright/test";

test("browser launches", async ({ page }) => {
  await page.setContent("<h1>DocProc</h1>");
  await expect(page.locator("h1")).toHaveText("DocProc");
});
