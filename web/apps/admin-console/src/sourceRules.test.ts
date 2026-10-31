import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative } from "node:path";
import { describe, expect, it } from "vitest";

/** Rules for the source of both apps and the shell that a type check cannot express. */
const webDir = join(__dirname, "..", "..", "..");
const roots = ["apps/admin-console/src", "apps/customer-portal/src", "packages/app-shell/src"].map((dir) => join(webDir, dir));

function sourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return name === "test" ? [] : sourceFiles(path);
    return /\.(ts|tsx)$/.test(name) && !/\.test\.tsx?$/.test(name) && !name.endsWith(".gen.ts") ? [path] : [];
  });
}

const files = roots.flatMap(sourceFiles).map((path) => ({ path: relative(webDir, path), text: readFileSync(path, "utf8") }));

describe("web source rules", () => {
  it("scans the sources", () => {
    expect(files.length).toBeGreaterThan(50);
  });

  it("builds no infinite-list key by hand: queryKeys.x.pages() is the one shape (docs/05)", () => {
    expect(files.filter((file) => /,\s*"pages"\s*\]/.test(file.text)).map((file) => file.path)).toEqual([]);
  });

  it("takes form controls from @docproc/ui instead of raw elements (docs/05)", () => {
    const raw = /<(button|input|select|table|dialog|textarea|label)[\s>]/;
    // Deliberate: the upload's hidden <input type="file"> behind its dropzone, and the dev sign-in's user cards (a whole
    // card is one button, which the ui Button's sizing does not fit).
    const allowed = new Set(["apps/customer-portal/src/components/UploadDialog.tsx", "packages/app-shell/src/layout/SignInPage.tsx"]);
    expect(files.filter((file) => file.path.endsWith(".tsx") && raw.test(file.text) && !allowed.has(file.path)).map((file) => file.path)).toEqual([]);
  });

  it("gives both apps a favicon, so no page load logs a 404 for /favicon.ico", () => {
    for (const app of ["admin-console", "customer-portal"]) {
      const html = readFileSync(join(webDir, "apps", app, "index.html"), "utf8");
      const href = /<link rel="icon"[^>]*href="\/([^"]+)"/.exec(html)?.[1];
      expect(href, app).toBeDefined();
      expect(existsSync(join(webDir, "apps", app, "public", href!)), `${app}/public/${href}`).toBe(true);
    }
  });
});
