/**
 * SDK Client Wrapper — convenience layer for API routes.
 * Retrieves the OpencodeClient for a managed instance and re-exports SDK types.
 */

import { getInstance } from "./process-manager";
import type { OpencodeClient } from "@opencode-ai/sdk/v2";

// Re-export SDK types for use in API routes
export type { OpencodeClient };
export type {
  Session,
  Message,
  Part,
  FileDiff,
} from "@opencode-ai/sdk/v2";

/**
 * Retrieve the OpencodeClient for a running managed instance.
 * Throws if the instance is not found or is dead.
 */
export function getClientForInstance(instanceId: string): OpencodeClient {
  const instance = getInstance(instanceId);
  if (!instance) {
    throw new Error(`Instance not found: ${instanceId}`);
  }
  if (instance.status === "dead") {
    throw new Error(`Instance is dead: ${instanceId}`);
  }
  return instance.client;
}
