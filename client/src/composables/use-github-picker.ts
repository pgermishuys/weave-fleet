import { onScopeDispose, shallowRef, toValue, watch, type MaybeRefOrGetter } from "vue"
import { fetchGitHubItemsByNumber, searchGitHubItems, type GitHubItemSummary } from "@/lib/github-items"

export interface PickerGroup {
  label: string
  items: GitHubItemSummary[]
}

const DEBOUNCE_MS = 200

/**
 * What the `#` picker offers for a repository: with nothing typed, open items assigned to the user and then the
 * most recently updated; with a number, that item first; with words, a search.
 */
export function useGitHubPicker(
  repository: MaybeRefOrGetter<{ owner: string; repo: string } | null>,
  query: MaybeRefOrGetter<string | null>,
) {
  const groups = shallowRef<PickerGroup[]>([])
  const isLoading = shallowRef(false)
  const error = shallowRef<string | null>(null)
  let controller: AbortController | null = null
  let timer: ReturnType<typeof setTimeout> | undefined

  async function load(owner: string, repo: string, typed: string): Promise<void> {
    controller?.abort()
    const current = new AbortController()
    controller = current
    isLoading.value = true
    error.value = null
    const scope = `repo:${owner}/${repo} is:open`
    try {
      let next: PickerGroup[]
      if (typed === "") {
        const [assigned, recent] = await Promise.all([
          searchGitHubItems(`${scope} assignee:@me sort:updated-desc`, { first: 5, signal: current.signal }),
          searchGitHubItems(`${scope} sort:updated-desc`, { first: 8, signal: current.signal }),
        ])
        const mine = new Set(assigned.items.map((item) => item.number))
        next = [
          { label: "Assigned to you", items: assigned.items },
          { label: "Recently updated", items: recent.items.filter((item) => !mine.has(item.number)) },
        ]
      } else if (/^\d+$/.test(typed)) {
        const [exact, matches] = await Promise.all([
          fetchGitHubItemsByNumber(owner, repo, [Number(typed)], current.signal),
          searchGitHubItems(`${scope} ${typed} sort:updated-desc`, { first: 8, signal: current.signal }),
        ])
        const found = new Set(exact.map((item) => item.number))
        next = [{ label: "Matches", items: [...exact, ...matches.items.filter((item) => !found.has(item.number))] }]
      } else {
        const matches = await searchGitHubItems(`${scope} ${typed} sort:updated-desc`, { first: 10, signal: current.signal })
        next = [{ label: "Matches", items: matches.items }]
      }
      if (current.signal.aborted) return
      groups.value = next.filter((group) => group.items.length > 0)
    } catch (loadError) {
      if (current.signal.aborted) return
      groups.value = []
      error.value = loadError instanceof Error ? loadError.message : "GitHub didn't answer."
    } finally {
      if (controller === current) isLoading.value = false
    }
  }

  watch(
    () => {
      const repo = toValue(repository)
      const typed = toValue(query)
      return repo && typed !== null ? `${repo.owner}/${repo.repo}\n${typed}` : null
    },
    (key, previous) => {
      clearTimeout(timer)
      const repo = toValue(repository)
      const typed = toValue(query)
      if (key === null || !repo || typed === null) {
        controller?.abort()
        groups.value = []
        isLoading.value = false
        error.value = null
        return
      }
      isLoading.value = true
      // Opening the picker asks straight away; typing waits for a pause.
      if (previous === null || previous === undefined) void load(repo.owner, repo.repo, typed)
      else timer = setTimeout(() => void load(repo.owner, repo.repo, typed), DEBOUNCE_MS)
    },
    { immediate: true },
  )

  onScopeDispose(() => {
    clearTimeout(timer)
    controller?.abort()
  })

  return { groups, isLoading, error }
}
