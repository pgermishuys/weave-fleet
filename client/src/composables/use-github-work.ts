import { computed, shallowRef } from "vue"
import { fetchGitHubWork, type GitHubWork } from "@/lib/github-items"

// One copy for the GitHub home and the GitHub panel's counts, fetched again only when it's this old.
const STALE_AFTER_MS = 60_000

const work = shallowRef<GitHubWork | null>(null)
const isLoading = shallowRef(false)
const error = shallowRef<string | null>(null)
const loadedAt = shallowRef(0)
let pending: Promise<void> | null = null

async function load(force = false): Promise<void> {
  if (!force && work.value && Date.now() - loadedAt.value < STALE_AFTER_MS) return
  if (pending) return pending
  pending = (async () => {
    isLoading.value = true
    error.value = null
    try {
      work.value = await fetchGitHubWork()
      loadedAt.value = Date.now()
    } catch (loadError) {
      error.value = loadError instanceof Error ? loadError.message : "Couldn't load your GitHub work."
    } finally {
      isLoading.value = false
      pending = null
    }
  })()
  return pending
}

/** What needs the user across the repositories they follow: review requests, their pull requests, assigned issues. */
export function useGitHubWork() {
  return {
    work: computed(() => work.value),
    isLoading: computed(() => isLoading.value),
    error: computed(() => error.value),
    /** When the copy on screen came from GitHub (ms since the epoch), or 0. */
    loadedAt: computed(() => loadedAt.value),
    load,
    refresh: () => load(true),
  }
}

/** Test-only: forgets the shared copy. */
export function _resetGitHubWorkForTesting(): void {
  work.value = null
  error.value = null
  isLoading.value = false
  loadedAt.value = 0
  pending = null
}
