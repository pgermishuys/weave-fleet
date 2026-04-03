// Signal to React that this is an act() environment.
// Required by @testing-library/react when running renderHook/render in jsdom.
if (typeof globalThis !== "undefined") {
  (globalThis as Record<string, unknown>).IS_REACT_ACT_ENVIRONMENT = true;
}
