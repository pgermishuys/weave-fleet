/**
 * Integration store — read/write storage for integration configuration.
 *
 * Stores tokens and settings per integration ID in the active profile's
 * integrations.json file:
 *   - Default profile: ~/.weave/integrations.json
 *   - Named profiles:  ~/.weave/profiles/<name>/integrations.json
 * Never throws — all errors are handled gracefully.
 */

import { existsSync, mkdirSync, readFileSync, writeFileSync } from "fs";
import { dirname } from "path";
import { log } from "./logger";
import { getProfileIntegrationsPath } from "./profile";

/** Per-integration configuration record */
export interface IntegrationConfig {
  token?: string;
  connectedAt?: string;
  [key: string]: unknown;
}

/** The shape of integrations.json on disk */
type IntegrationsFile = Record<string, IntegrationConfig>;

/**
 * Returns the path to the integrations config file for the active profile.
 * Accepts an optional override for testing.
 */
export function getIntegrationsFilePath(override?: string): string {
  return override ?? getProfileIntegrationsPath();
}

function readFile(filePath: string): IntegrationsFile {
  if (!existsSync(filePath)) {
    return {};
  }

  try {
    const content = readFileSync(filePath, "utf-8");
    const parsed = JSON.parse(content);
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      log.warn("integration-store", "integrations.json is not a valid object");
      return {};
    }
    return parsed as IntegrationsFile;
  } catch (err) {
    log.warn("integration-store", "Failed to read integrations.json", {
      path: filePath,
      err,
    });
    return {};
  }
}

function writeFile(filePath: string, data: IntegrationsFile): boolean {
  try {
    const dir = dirname(filePath);
    if (!existsSync(dir)) {
      mkdirSync(dir, { recursive: true });
    }
    writeFileSync(filePath, JSON.stringify(data, null, 2), "utf-8");
    return true;
  } catch (err) {
    log.warn("integration-store", "Failed to write integrations.json", {
      path: filePath,
      err,
    });
    return false;
  }
}

/**
 * Get the configuration for a specific integration.
 * Returns null if not configured or on error.
 */
export function getIntegrationConfig(
  id: string,
  filePath?: string
): IntegrationConfig | null {
  const path = getIntegrationsFilePath(filePath);
  const data = readFile(path);
  return data[id] ?? null;
}

/**
 * Save (upsert) the configuration for a specific integration.
 * Returns true on success, false on failure.
 */
export function setIntegrationConfig(
  id: string,
  config: IntegrationConfig,
  filePath?: string
): boolean {
  const path = getIntegrationsFilePath(filePath);
  const data = readFile(path);
  data[id] = { ...config, connectedAt: config.connectedAt ?? new Date().toISOString() };
  return writeFile(path, data);
}

/**
 * Remove the configuration for a specific integration.
 * Returns true on success, false on failure.
 */
export function removeIntegrationConfig(
  id: string,
  filePath?: string
): boolean {
  const path = getIntegrationsFilePath(filePath);
  const data = readFile(path);
  if (!(id in data)) {
    return true; // nothing to remove
  }
  delete data[id];
  return writeFile(path, data);
}

/**
 * Get all integration configurations.
 * Returns an empty object if the file doesn't exist or on error.
 */
export function getAllIntegrationConfigs(
  filePath?: string
): IntegrationsFile {
  const path = getIntegrationsFilePath(filePath);
  return readFile(path);
}
