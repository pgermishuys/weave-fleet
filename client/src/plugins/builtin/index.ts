import type { FleetBuiltInPluginModule } from "../types";
import { registerIntegrationCompatibility } from "@/integrations/registry";
import { githubManifest } from "@/integrations/github/manifest";
import { githubPluginManifest } from "./github";

registerIntegrationCompatibility(githubManifest);

export const builtInPlugins: readonly FleetBuiltInPluginModule[] = [
  {
    manifest: githubPluginManifest,
  },
];
