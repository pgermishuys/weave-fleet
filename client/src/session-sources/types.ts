import type { ComponentType } from "react";
import type { SessionSourceDescriptor } from "@/lib/api-types";

export interface SessionSourceUiContribution {
  pluginId: string;
  id: string;
  sourceKey: {
    providerId: string;
    sourceType: string;
  };
  label?: string;
  description?: string;
  icon?: ComponentType<{ className?: string; size?: number }>;
  order?: number;
  formComponent?: ComponentType<{ providerId: string; sourceType: string }>;
}

export interface RegisteredSessionSource {
  descriptor: SessionSourceDescriptor;
  pluginId?: string;
  contributionId?: string;
  label: string;
  description: string | null;
  icon?: ComponentType<{ className?: string; size?: number }>;
  order: number;
  formComponent?: ComponentType<{ providerId: string; sourceType: string }>;
}
