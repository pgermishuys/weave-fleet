import { registerIntegration } from "@/integrations/registry";
import { githubManifest } from "./manifest";

// Self-register when this module is imported
registerIntegration(githubManifest);

export { githubManifest };
