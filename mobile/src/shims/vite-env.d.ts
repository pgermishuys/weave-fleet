// Type-only imports from the web client reach files that use Vite's import.meta additions. The app never runs them.
interface ImportMeta {
  readonly env: Record<string, string> & { readonly DEV: boolean; readonly PROD: boolean };
  readonly hot?: { on(event: string, callback: (payload: never) => void): void };
}
