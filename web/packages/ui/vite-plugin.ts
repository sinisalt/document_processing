import path from "node:path";
import { fileURLToPath } from "node:url";
import type { Plugin } from "vite";

const uiSourceRoot = fileURLToPath(new URL("./src", import.meta.url));

/**
 * The shadcn-generated components import each other and `cn` through the `@/` alias of this package
 * (components.json). An app that bundles them from source wants `@/` for its own `src`, so a plain `resolve.alias`
 * entry would send `@/lib/utils` to the wrong place for one side or the other. This plugin resolves `@/…` by
 * *importer*: files of this package get `packages/ui/src`, everything else gets `appSourceDir` (or this package
 * too when no app dir is given, as in a package's own vitest config). Use it *instead of* a `@` entry in
 * `resolve.alias`:
 *
 *   plugins: [tanstackRouter(...), react(), tailwindcss(), docprocAlias(fileURLToPath(new URL("./src", import.meta.url)))]
 *
 * The matching tsconfig rule is a second `paths` fallback entry (see apps/*\/tsconfig.json).
 */
export function docprocAlias(appSourceDir?: string): Plugin {
  return {
    name: "docproc-alias",
    enforce: "pre",
    resolveId(source, importer) {
      if (!source.startsWith("@/")) return null;
      const rest = source.slice(2);
      const fromUi = importer ? path.resolve(importer).startsWith(uiSourceRoot + path.sep) : false;
      const base = fromUi || !appSourceDir ? uiSourceRoot : appSourceDir;
      return this.resolve(path.join(base, rest), importer, { skipSelf: true });
    },
  };
}
