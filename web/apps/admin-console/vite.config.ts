import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
import { tanstackRouter } from "@tanstack/router-plugin/vite";
import { docprocAlias } from "@docproc/ui/vite";
import { fileURLToPath, URL } from "node:url";

// VITE_API_PROXY points the dev proxy at the API (default: the dependency-free dev mode on 5100, README "Running without Docker").
const apiProxy = process.env.VITE_API_PROXY ?? "http://localhost:5100";
const srcDir = fileURLToPath(new URL("./src", import.meta.url));

export default defineConfig({
  plugins: [
    // File-based routes: src/routes/** → src/routeTree.gen.ts (committed; regenerated on dev/build). Must precede react().
    tanstackRouter({ target: "react", autoCodeSplitting: true, routesDirectory: "./src/routes", generatedRouteTree: "./src/routeTree.gen.ts" }),
    react(),
    tailwindcss(),
    // `@/` is this app's src, except inside @docproc/ui where it is that package's src (see packages/ui/vite.ts).
    docprocAlias(srcDir),
  ],
  server: {
    proxy: {
      "/api": { target: apiProxy, changeOrigin: true },
      "/dev": { target: apiProxy, changeOrigin: true },
    },
  },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test-setup.ts"],
  },
});
