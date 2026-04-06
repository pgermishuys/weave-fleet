/// <reference types="vite/client" />

interface ImportMetaEnv {
  readonly VITE_API_BASE_URL: string;
  readonly VITE_APP_VERSION: string;
  readonly VITE_COMMIT_SHA: string;
  readonly VITE_WEAVE_PROFILE: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
