/**
 * Auth store reader — reads OpenCode's auth.json to determine connected providers.
 *
 * This is read-only — we never write to auth.json.
 * All errors are handled gracefully and result in an empty array.
 */

import { existsSync, readFileSync } from "fs";
import { getAuthJsonPath } from "@/cli/config-paths";
import { log } from "./logger";

/** Auth entry types matching OpenCode's auth.json format */
export type AuthType = "api" | "oauth" | "wellknown";

export interface ConnectedProvider {
  id: string;          // Provider ID matching auth.json key (e.g. "anthropic")
  authType: AuthType;  // The type field from the auth entry
}

/**
 * Read OpenCode's auth.json and return the list of connected providers.
 * Returns an empty array if the file doesn't exist, is unreadable, or is malformed.
 * Never throws — all errors are logged and result in an empty array.
 */
export function getConnectedProviders(authJsonPath?: string): ConnectedProvider[] {
  const filePath = authJsonPath ?? getAuthJsonPath();

  if (!existsSync(filePath)) {
    return [];
  }

  try {
    const content = readFileSync(filePath, "utf-8");
    const parsed = JSON.parse(content);

    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      log.warn("auth-store", "auth.json is not a valid object");
      return [];
    }

    const providers: ConnectedProvider[] = [];
    for (const [key, value] of Object.entries(parsed)) {
      if (value && typeof value === "object" && !Array.isArray(value)) {
        const entry = value as Record<string, unknown>;
        const authType = entry.type;
        if (authType === "api" || authType === "oauth" || authType === "wellknown") {
          providers.push({ id: key, authType });
        }
      }
    }
    return providers;
  } catch (err) {
    log.warn("auth-store", "Failed to read auth.json", { path: filePath, err });
    return [];
  }
}
