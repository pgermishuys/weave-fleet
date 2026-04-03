import type { IntegrationManifest } from "./types";

const manifests: IntegrationManifest[] = [];

export function registerIntegration(manifest: IntegrationManifest): void {
  const existing = manifests.findIndex((m) => m.id === manifest.id);
  if (existing >= 0) {
    manifests[existing] = manifest;
  } else {
    manifests.push(manifest);
  }
}

export function getIntegrations(): readonly IntegrationManifest[] {
  return manifests;
}

export function getIntegration(id: string): IntegrationManifest | undefined {
  return manifests.find((m) => m.id === id);
}

export function getConnectedIntegrations(): IntegrationManifest[] {
  return manifests.filter((m) => m.isConfigured());
}
