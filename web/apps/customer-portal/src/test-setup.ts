import "@testing-library/jest-dom/vitest";

// jsdom has no matchMedia; useIsMobile (the inbox columns, the review layout) reads it. Desktop unless a test says otherwise.
window.matchMedia ??= ((query: string) => ({
  matches: false,
  media: query,
  onchange: null,
  addEventListener: () => undefined,
  removeEventListener: () => undefined,
  addListener: () => undefined,
  removeListener: () => undefined,
  dispatchEvent: () => false,
})) as typeof window.matchMedia;
