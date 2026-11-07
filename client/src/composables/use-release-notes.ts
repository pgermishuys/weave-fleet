import { readonly, shallowRef } from "vue";
import { useMachineTarget, type MachineTarget } from "@/lib/machine-target";
import type { ReleaseNote } from "@/lib/release-notes";

interface ReleaseNotesResponse {
  releases: ReleaseNote[];
  fetchedAt: string | null;
  error: string | null;
}

// Shared per machine: the notes don't change while Fleet runs, and Fleet keeps them on disk, so asking again is cheap.
function createReleaseNotes({ api }: MachineTarget) {
  const releases = shallowRef<ReleaseNote[]>([]);
  const fetchedAt = shallowRef<string | null>(null);
  const error = shallowRef<string | null>(null);
  const isLoading = shallowRef(false);

  async function load(): Promise<void> {
    isLoading.value = true;
    try {
      const { data, error: apiError } = await api.GET("/api/update/releases");
      if (apiError || !data) {
        error.value = "Fleet couldn't load the release notes.";
        return;
      }
      const result = data as ReleaseNotesResponse;
      releases.value = result.releases;
      fetchedAt.value = result.fetchedAt;
      error.value = result.error;
    } catch {
      error.value = "Fleet couldn't load the release notes.";
    } finally {
      isLoading.value = false;
    }
  }

  return {
    releases: readonly(releases),
    fetchedAt: readonly(fetchedAt),
    error: readonly(error),
    isLoading: readonly(isLoading),
    load,
  };
}

const instances = new Map<string, ReturnType<typeof createReleaseNotes>>();

/** The notes of Fleet's recent releases, newest first, for What's new in Settings → Updates & about. */
export function useReleaseNotes() {
  const target = useMachineTarget();
  let notes = instances.get(target.key);
  if (!notes) {
    notes = createReleaseNotes(target);
    instances.set(target.key, notes);
  }
  return notes;
}
