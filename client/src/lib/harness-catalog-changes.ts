import { getCurrentScope, onScopeDispose } from "vue";
import { onGlobalEvent } from "@/composables/use-signalr-socket";
import type { DomainEvent } from "@/lib/domain-events";

/**
 * Event the server pushes on the "sessions" topic when what a harness offers in a folder (agents, models, commands)
 * changed while it ran: the user added an agent file, edited config or signed in to a provider. Only harnesses that
 * can tell send it (OpenCode 2); the others' lists refresh when they're reopened.
 */
export const HARNESS_CATALOG_CHANGED = "harness.catalog_changed";

export interface HarnessCatalogChange {
  harnessType: string;
  directory: string;
  /** The folder is where quick chats run, which the composer asks for without naming a folder. */
  quickChat: boolean;
  /** The profiles whose catalog this is, by id, with "none" for sessions without one. */
  profileIds: string[];
  /** The sessions in the folder that get this catalog. */
  sessionIds: string[];
}

function strings(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string") : [];
}

export function parseCatalogChange(payload: unknown): HarnessCatalogChange | null {
  if (!payload || typeof payload !== "object") return null;
  const wire = payload as Record<string, unknown>;
  if (typeof wire.harnessType !== "string" || typeof wire.directory !== "string") return null;
  return {
    harnessType: wire.harnessType,
    directory: wire.directory,
    quickChat: wire.quickChat === true,
    profileIds: strings(wire.profileIds),
    sessionIds: strings(wire.sessionIds),
  };
}

function trimFolder(directory: string): string {
  return directory.length > 1 ? directory.replace(/[\\/]+$/, "") : directory;
}

/**
 * Whether `change` is to the catalog asked for with `harnessType`, `directory` (null for a quick chat's) and
 * `profile` (an id, "none", or undefined for the harness's default, which the browser may not know yet).
 */
export function isCatalogChangeFor(
  change: HarnessCatalogChange,
  harnessType: string,
  directory: string | null,
  profile: string | undefined,
): boolean {
  if (change.harnessType !== harnessType) return false;
  const sameFolder = directory ? trimFolder(directory) === trimFolder(change.directory) : change.quickChat;
  return sameFolder && (profile === undefined || change.profileIds.includes(profile));
}

/** Calls `handler` for every catalog change pushed until the returned function is called. */
export function listenForCatalogChanges(handler: (change: HarnessCatalogChange) => void): () => void {
  return onGlobalEvent("sessions", (event: DomainEvent) => {
    if ((event.type as string) !== HARNESS_CATALOG_CHANGED) return;
    const change = parseCatalogChange(event.payload);
    if (change) handler(change);
  });
}

/** Calls `handler` for every catalog change pushed while the calling scope lives. */
export function onCatalogChange(handler: (change: HarnessCatalogChange) => void): () => void {
  const unsubscribe = listenForCatalogChanges(handler);
  if (getCurrentScope()) onScopeDispose(unsubscribe);
  return unsubscribe;
}
