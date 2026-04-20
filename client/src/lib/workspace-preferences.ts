export interface WorkspacePreferences {
  displayName: string;
  preferredRootPath: string;
  autoRefreshRepositories: boolean;
}

export const workspacePreferencesStorageKey = "weave:settings:workspace";

export function createDefaultWorkspacePreferences(): WorkspacePreferences {
  return {
    displayName: "Workspace",
    preferredRootPath: "",
    autoRefreshRepositories: true,
  };
}

export function readWorkspacePreferences(storage?: Storage | null): WorkspacePreferences {
  if (!storage) {
    return createDefaultWorkspacePreferences();
  }

  try {
    const raw = storage.getItem(workspacePreferencesStorageKey);
    if (!raw) {
      return createDefaultWorkspacePreferences();
    }

    const parsed = JSON.parse(raw) as Partial<WorkspacePreferences>;
    return {
      displayName: parsed.displayName?.trim() || createDefaultWorkspacePreferences().displayName,
      preferredRootPath: parsed.preferredRootPath?.trim() || "",
      autoRefreshRepositories: parsed.autoRefreshRepositories ?? true,
    };
  } catch {
    return createDefaultWorkspacePreferences();
  }
}

export function writeWorkspacePreferences(next: WorkspacePreferences, storage?: Storage | null): void {
  if (!storage) {
    return;
  }

  try {
    storage.setItem(workspacePreferencesStorageKey, JSON.stringify(next));
  } catch {
    // localStorage unavailable
  }
}
