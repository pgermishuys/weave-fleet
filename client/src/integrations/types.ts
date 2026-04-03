import type { ComponentType } from "react";

/** The boundary type between integrations and core Fleet */
export interface ContextSource {
  type: string; // "github-issue", "github-pr"
  url: string; // canonical URL
  title: string; // display title
  body: string; // markdown content → becomes initial prompt context
  metadata: Record<string, unknown>; // source-specific data (labels, comments, diff stats, etc.)
}

/** Each integration registers a manifest */
export interface IntegrationManifest {
  id: string; // "github"
  name: string; // "GitHub"
  icon: ComponentType<{ size?: number }>; // Lucide icon or custom
  browserComponent: ComponentType; // Main browser UI
  isConfigured: () => boolean; // Checks if token/config exists
  settingsComponent?: ComponentType; // Settings panel for this integration
  resolveContext: (url: string) => Promise<ContextSource | null>; // URL → context
}

/** Connection status for an integration */
export type IntegrationStatus = "connected" | "disconnected" | "error";

/** Runtime state per integration */
export interface IntegrationState {
  manifest: IntegrationManifest;
  status: IntegrationStatus;
}
