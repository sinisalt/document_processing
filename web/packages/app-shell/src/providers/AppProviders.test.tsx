import { render } from "@testing-library/react";
import { ThemeProvider } from "next-themes";
import { afterEach, beforeAll, vi } from "vitest";
import { I18nextProvider } from "react-i18next";
import { createI18n } from "../i18n";
import { ShellToaster, themeProviderProps } from "./AppProviders";

const SCRIPT_WARNING = "Encountered a script tag while rendering React component";

function scriptWarnings(spy: { mock: { calls: unknown[][] } }): number {
  return spy.mock.calls.filter((args) => args.some((arg) => typeof arg === "string" && arg.includes(SCRIPT_WARNING))).length;
}

beforeAll(() => {
  // next-themes reads the system preference; jsdom has no matchMedia.
  window.matchMedia ??= ((query: string) => ({ matches: false, media: query, onchange: null, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => false })) as typeof window.matchMedia;
});

// React warns about a script tag once per page load, so the case without the warning must run first.
describe("the shell's ThemeProvider", () => {
  afterEach(() => vi.restoreAllMocks());

  it("renders next-themes' script as a data block, so React 19 does not warn about it", () => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    const { container } = render(
      <ThemeProvider {...themeProviderProps}>
        <p>content</p>
      </ThemeProvider>,
    );
    expect(scriptWarnings(spy)).toBe(0);
    expect(container.querySelector("script")).toHaveAttribute("type", "application/json");
  });

  it("would warn without the scriptProps (so the check above can fail)", () => {
    const spy = vi.spyOn(console, "error").mockImplementation(() => {});
    const { scriptProps: _scriptProps, ...withoutScriptProps } = themeProviderProps;
    render(
      <ThemeProvider {...withoutScriptProps}>
        <p>content</p>
      </ThemeProvider>,
    );
    expect(scriptWarnings(spy)).toBe(1);
  });
});

describe("the shell's toaster", () => {
  it("labels the notification region in the UI language", () => {
    const i18n = createI18n({ lng: "de" });
    const { container } = render(
      <I18nextProvider i18n={i18n}>
        <ShellToaster />
      </I18nextProvider>,
    );
    expect(container.querySelector("section")?.getAttribute("aria-label")).toMatch(/^Benachrichtigungen/);
  });
});
