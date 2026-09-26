import { computed, nextTick, onMounted, type ComputedRef, type MaybeRefOrGetter, toValue } from "vue"
import { useRouter } from "@tanstack/vue-router"
import type { SessionListItem } from "@/api/client"
import type { GitHubSessionSourcePreset } from "@/lib/github-session-source"
import { useSessions } from "@/composables/use-sessions"
import { useSessionsStore } from "@/stores/sessions"
import { useSidebarStore } from "@/stores/sidebar"
import { useSmartLinksStore } from "@/stores/smart-links"
import { useWorkspaceUiStore } from "@/stores/workspace-ui"

/**
 * Fleet's side of a GitHub page: the sessions working on each pull request or issue (from the sessions'
 * links), opening one, and starting a new one from an item.
 */
export function useGitHubSessions() {
  const router = useRouter()
  const smartLinks = useSmartLinksStore()
  const sessionsStore = useSessionsStore()
  const sidebar = useSidebarStore()
  const workspaceUi = useWorkspaceUiStore()

  // The sessions list isn't on screen here, so load it for the names and statuses.
  useSessions({ retentionStatus: "active" })
  onMounted(() => {
    void smartLinks.ensureHeaderLinksLoaded()
  })

  /** The sessions working on `owner/repo#123`, most recently active first. */
  function sessionsFor(resourceId: string): SessionListItem[] {
    const ids = new Set(smartLinks.sessionsFor(resourceId))
    if (ids.size === 0) return []
    return sessionsStore.sessions
      .filter((item) => ids.has(item.session.id) && item.retentionStatus !== "archived")
      .sort((a, b) => lastActive(b) - lastActive(a))
  }

  function useSessionsFor(resourceId: MaybeRefOrGetter<string | null>): ComputedRef<SessionListItem[]> {
    return computed(() => {
      const id = toValue(resourceId)
      return id ? sessionsFor(id) : []
    })
  }

  function openSession(item: SessionListItem): void {
    void router.navigate({
      to: "/sessions/$id",
      params: { id: item.session.id },
      search: { instanceId: item.instanceId, parentSessionId: undefined },
    })
  }

  async function startSession(preset: GitHubSessionSourcePreset): Promise<void> {
    sidebar.setPanelCollapsed(false)
    sidebar.setActiveRail("sessions")
    await nextTick()
    workspaceUi.setNewSessionInitialSource(preset)
    void router.navigate({ to: "/sessions/new", search: { projectId: undefined, source: undefined } })
  }

  return { sessionsFor, useSessionsFor, openSession, startSession }
}

function lastActive(item: SessionListItem): number {
  const value: unknown = item.session.time?.updated ?? item.session.time?.created
  return typeof value === "number" ? value : Date.parse(String(value ?? "")) || 0
}
