import { readonly, shallowRef } from "vue";
import { api } from "@/api/client";
import type { ReleaseNote } from "@/lib/release-notes";

interface ReleaseNotesResponse {
  releases: ReleaseNote[];
  fetchedAt: string | null;
  error: string | null;
}

// Shared: the notes don't change while Fleet runs, and Fleet keeps them on disk, so asking again is cheap.
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

/** The notes of Fleet's recent releases, newest first, for What's new in Settings → System. */
export function useReleaseNotes() {
  return {
    releases: readonly(releases),
    fetchedAt: readonly(fetchedAt),
    error: readonly(error),
    isLoading: readonly(isLoading),
    load,
  };
}
