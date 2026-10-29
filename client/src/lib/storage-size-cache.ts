/** A folder size Fleet measured, as the size endpoint answers it. */
export interface StorageSize {
  bytes: number;
  complete: boolean;
}

/** How long a measured size is shown again without measuring it again; each one is a walk of the folder. */
export const STORAGE_SIZE_TTL_MS = 5 * 60 * 1000;

/** Per machine, then per workspace: the size and when it was measured. Lives as long as the page, so a return to Folders reuses it. */
const measured = new Map<string, Map<string, { size: StorageSize; at: number }>>();

/** The sizes measured on this machine within the last few minutes, by workspace id. */
export function recentStorageSizes(machine: string, now = Date.now()): Record<string, StorageSize> {
  const recent: Record<string, StorageSize> = {};
  for (const [workspaceId, entry] of measured.get(machine) ?? []) {
    if (now - entry.at < STORAGE_SIZE_TTL_MS) recent[workspaceId] = entry.size;
  }
  return recent;
}

export function rememberStorageSize(machine: string, workspaceId: string, size: StorageSize, now = Date.now()): void {
  const forMachine = measured.get(machine) ?? new Map();
  forMachine.set(workspaceId, { size, at: now });
  measured.set(machine, forMachine);
}

/** Drops every remembered size; for tests. */
export function forgetStorageSizes(): void {
  measured.clear();
}
