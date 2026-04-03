/**
 * Profile resolution module — centralizes all profile-aware path/port resolution.
 *
 * A "profile" is a named isolation context. Each profile gets its own:
 *   - SQLite database
 *   - Workspace root directory
 *   - Integrations config file
 *   - OpenCode port range (deterministic, hash-based)
 *   - Default server port
 *
 * The default profile (WEAVE_PROFILE unset or "default") is fully backward-compatible
 * with existing paths: ~/.weave/fleet.db, ports 4097–4200, server port 3000.
 *
 * Named profiles store data under ~/.weave/profiles/<name>/.
 *
 * Environment variable overrides always take precedence over profile-derived values:
 *   WEAVE_DB_PATH           → overrides database path
 *   WEAVE_WORKSPACE_ROOT    → overrides workspace root
 *   PORT                    → overrides server port
 *   WEAVE_PORT_RANGE_START  → overrides OpenCode port range base (escape hatch for hash collisions)
 */

import { homedir } from "os";
import { resolve } from "path";

// ─── Default constants (backward-compatible) ─────────────────────────────────

const DEFAULT_PORT_START = 4097;
const DEFAULT_PORT_END = 4200;
const PORT_RANGE_SIZE = DEFAULT_PORT_END - DEFAULT_PORT_START + 1; // 104
const DEFAULT_SERVER_PORT = 3000;

// Port hash search space: 5000–59999, stepping by PORT_RANGE_SIZE + 1 buffer
// This gives (59999 - 5000) / 105 ≈ 523 possible slots, well clear of the default range.
const HASH_PORT_MIN = 5000;
const HASH_PORT_MAX = 59999;

// ─── Profile name ─────────────────────────────────────────────────────────────

/**
 * Returns the active profile name.
 * Reads the WEAVE_PROFILE environment variable; returns "default" if unset.
 */
export function getProfileName(): string {
  return process.env.WEAVE_PROFILE || "default";
}

/**
 * Returns true when the active profile is the default (backward-compatible) profile.
 */
export function isDefaultProfile(): boolean {
  return getProfileName() === "default";
}

// ─── Profile name validation ──────────────────────────────────────────────────

/**
 * Validates a profile name.
 * Valid: 1–32 chars, lowercase alphanumeric + hyphens only.
 * Must start and end with an alphanumeric character (no leading/trailing hyphens).
 * Throws an Error with a descriptive message if invalid.
 */
export function validateProfileName(name: string): void {
  if (!name || name.length === 0) {
    throw new Error("Profile name must not be empty.");
  }
  if (name.length > 32) {
    throw new Error(
      `Profile name must be 32 characters or fewer (got ${name.length}).`
    );
  }
  // Single char: must be alphanumeric. Multi-char: must start and end with alphanumeric,
  // middle may contain hyphens.
  if (!/^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/.test(name)) {
    throw new Error(
      `Profile name must contain only lowercase alphanumeric characters and hyphens, and must start and end with an alphanumeric character (got "${name}").`
    );
  }
}

// ─── Profile directory ────────────────────────────────────────────────────────

/**
 * Returns the root directory for the active profile's data files.
 * - Default profile: ~/.weave  (backward compat)
 * - Named profiles:  ~/.weave/profiles/<name>
 *
 * Validates the profile name at runtime as a defense-in-depth guard against
 * path traversal if the server is started without the launcher's validation.
 */
export function getProfileDir(): string {
  if (isDefaultProfile()) {
    return resolve(homedir(), ".weave");
  }
  const name = getProfileName();
  validateProfileName(name);
  return resolve(homedir(), ".weave", "profiles", name);
}

// ─── Paths ────────────────────────────────────────────────────────────────────

/**
 * Returns the SQLite database path for the active profile.
 * Respects the WEAVE_DB_PATH env var override (explicit override always wins).
 */
export function getProfileDbPath(): string {
  if (process.env.WEAVE_DB_PATH) {
    return resolve(process.env.WEAVE_DB_PATH);
  }
  return resolve(getProfileDir(), "fleet.db");
}

/**
 * Returns the workspace root directory for the active profile.
 * Respects the WEAVE_WORKSPACE_ROOT env var override.
 */
export function getProfileWorkspaceRoot(): string {
  if (process.env.WEAVE_WORKSPACE_ROOT) {
    return resolve(process.env.WEAVE_WORKSPACE_ROOT);
  }
  return resolve(getProfileDir(), "workspaces");
}

/**
 * Returns the integrations config file path for the active profile.
 */
export function getProfileIntegrationsPath(): string {
  return resolve(getProfileDir(), "integrations.json");
}

// ─── Port range ───────────────────────────────────────────────────────────────

/**
 * Simple deterministic hash of a string (djb2 variant).
 * Returns a non-negative 32-bit integer.
 */
function hashString(s: string): number {
  let h = 5381;
  for (let i = 0; i < s.length; i++) {
    h = ((h << 5) + h + s.charCodeAt(i)) >>> 0; // keep unsigned 32-bit
  }
  return h;
}

/**
 * Returns the OpenCode port range for the active profile.
 * - Default profile: { start: 4097, end: 4200 }  (backward compat)
 * - Named profiles: deterministic range derived from the profile name hash,
 *   guaranteed not to overlap the default range.
 * - WEAVE_PORT_RANGE_START env var overrides the base (escape hatch for collisions).
 */
export function getProfilePortRange(): { start: number; end: number } {
  if (isDefaultProfile()) {
    return { start: DEFAULT_PORT_START, end: DEFAULT_PORT_END };
  }

  // Explicit override via env var
  if (process.env.WEAVE_PORT_RANGE_START) {
    const base = parseInt(process.env.WEAVE_PORT_RANGE_START, 10);
    if (Number.isFinite(base) && base > 0) {
      return { start: base, end: base + PORT_RANGE_SIZE - 1 };
    }
  }

  // Compute hash-derived base port in [HASH_PORT_MIN, HASH_PORT_MAX - PORT_RANGE_SIZE]
  const name = getProfileName();
  const h = hashString(name);

  // Number of slots available in the hash space (each slot is PORT_RANGE_SIZE wide)
  const slotCount = Math.floor(
    (HASH_PORT_MAX - HASH_PORT_MIN - PORT_RANGE_SIZE + 1) / PORT_RANGE_SIZE
  );
  const slot = h % slotCount;
  let base = HASH_PORT_MIN + slot * PORT_RANGE_SIZE;

  // Avoid collision with the default range [4097, 4200].
  // Since HASH_PORT_MIN=5000 > DEFAULT_PORT_END=4200, this is already guaranteed.
  // But add an explicit guard for future-proofing.
  if (
    base <= DEFAULT_PORT_END &&
    base + PORT_RANGE_SIZE - 1 >= DEFAULT_PORT_START
  ) {
    base = DEFAULT_PORT_END + 1;
  }

  return { start: base, end: base + PORT_RANGE_SIZE - 1 };
}

// ─── Server port ──────────────────────────────────────────────────────────────

/**
 * Returns the default server (Next.js) port for the active profile.
 * - Default profile: 3000  (backward compat)
 * - Named profiles: base - 1 of the OpenCode port range (deterministic)
 * - PORT env var always takes precedence.
 */
export function getProfileServerPort(): number {
  if (process.env.PORT) {
    const p = parseInt(process.env.PORT, 10);
    if (Number.isFinite(p) && p > 0) return p;
  }

  if (isDefaultProfile()) {
    return DEFAULT_SERVER_PORT;
  }

  const { start } = getProfilePortRange();
  // Use start - 1 as the server port; if that hits a reserved port, use start + PORT_RANGE_SIZE
  const candidate = start - 1;
  if (candidate > 1024) return candidate;
  return start + PORT_RANGE_SIZE;
}
