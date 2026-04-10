import type { ComponentType } from "react";
import type { FleetPluginTrustLevel } from "@/plugins/types";
import type { SessionSourceSelection } from "@/lib/api-types";

/**
 * Transitional compatibility types.
 *
 * The plugin host in `client/src/plugins/**` is the canonical extension seam.
 * Keep these integration-flavored shapes only while legacy GitHub callers are
 * being migrated off the old vocabulary.
 */

/** The boundary type between integrations and core Fleet */
export interface ContextSource {
  type: string; // display-only source label
  url: string; // canonical URL
  title: string; // display title
  source: SessionSourceSelection;
  metadata: Record<string, unknown>; // presentation-only source metadata
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
  pluginDescriptor?: {
    trustLevel?: FleetPluginTrustLevel;
    hasBackend?: boolean;
  };
}

/** Connection status for an integration */
export type IntegrationStatus = "connected" | "disconnected" | "error";

/** Runtime state per integration */
export interface IntegrationState {
  manifest: IntegrationManifest;
  status: IntegrationStatus;
}
