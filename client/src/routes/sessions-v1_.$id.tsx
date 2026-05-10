import { createFileRoute } from "@tanstack/vue-router";
import { computed, defineComponent, shallowRef, watch } from "vue";
import { ArrowLeft, Bot } from "lucide-vue-next";
import { storeToRefs } from "pinia";
import ActivityStream from "@/components/session/ActivityStream.vue";
import Composer from "@/components/session/Composer.vue";
import SessionDetailHeader from "@/components/session/SessionDetailHeader.vue";
import { incrementPendingPrompts, useSentPrompts } from "@/composables/use-send-prompt";
import { apiFetch } from "@/lib/api-client";
import type { SessionListItem, SessionOrigin } from "@/lib/api-types";
import { dispatchSessionV1Upsert } from "@/lib/session-sync-v1";
import { useSessionsV1Store } from "@/stores/sessions-v1";

function normalizeRetentionStatus(value: string | null | undefined): "active" | "archived" {
  return value === "archived" ? "archived" : "active";
}

interface SessionDetailResponse {
  id?: string | null;
  instanceId?: string | null;
  workspaceId?: string | null;
  workspaceDirectory?: string | null;
  workspaceDisplayName?: string | null;
  sourceDirectory?: string | null;
  isolationStrategy?: string | null;
  branch?: string | null;
  title?: string | null;
  status?: string | null;
  activityStatus?: string | null;
  lifecycleStatus?: string | null;
  retentionStatus?: string | null;
  origin?: SessionOrigin | null;
}

function getStringField(
  value: Record<string, unknown>,
  camelKey: string,
  pascalKey: string,
): string | null | undefined {
  const candidate = value[camelKey] ?? value[pascalKey];
  return typeof candidate === "string" ? candidate : candidate == null ? null : undefined;
}

function normalizeSessionDetailResponse(payload: unknown): SessionDetailResponse {
  if (!payload || typeof payload !== "object") {
    return {};
  }

  const value = payload as Record<string, unknown>;

  const originPayload = value.origin ?? value.Origin;
  const origin = originPayload && typeof originPayload === "object"
    ? {
      sourceType: getStringField(originPayload as Record<string, unknown>, "sourceType", "SourceType") ?? "",
      title: getStringField(originPayload as Record<string, unknown>, "title", "Title") ?? null,
      resourceUrl: getStringField(originPayload as Record<string, unknown>, "resourceUrl", "ResourceUrl") ?? null,
      resourceId: getStringField(originPayload as Record<string, unknown>, "resourceId", "ResourceId") ?? null,
      providerId: getStringField(originPayload as Record<string, unknown>, "providerId", "ProviderId") ?? "",
    } satisfies SessionOrigin
    : originPayload == null
      ? null
      : undefined;

  return {
    id: getStringField(value, "id", "Id"),
    instanceId: getStringField(value, "instanceId", "InstanceId"),
    workspaceId: getStringField(value, "workspaceId", "WorkspaceId"),
    workspaceDirectory: getStringField(value, "workspaceDirectory", "WorkspaceDirectory"),
    workspaceDisplayName: getStringField(value, "workspaceDisplayName", "WorkspaceDisplayName"),
    sourceDirectory: getStringField(value, "sourceDirectory", "SourceDirectory"),
    isolationStrategy: getStringField(value, "isolationStrategy", "IsolationStrategy"),
    branch: getStringField(value, "branch", "Branch"),
    title: getStringField(value, "title", "Title"),
    status: getStringField(value, "status", "Status"),
    activityStatus: getStringField(value, "activityStatus", "ActivityStatus"),
    lifecycleStatus: getStringField(value, "lifecycleStatus", "LifecycleStatus"),
    retentionStatus: getStringField(value, "retentionStatus", "RetentionStatus"),
    origin,
  };
}

function normalizeLifecycleStatus(value: string | null | undefined): "running" | "completed" | "stopped" | "error" | "disconnected" | null {
  switch (value) {
    case "active":
    case "idle":
    case "waiting_input":
    case "running":
      return "running";
    case "complete":
    case "completed":
      return "completed";
    case "error":
      return "error";
    case "disconnected":
      return "disconnected";
    case "stopped":
      return "stopped";
    default:
      return null;
  }
}

const SessionV1DetailPage = defineComponent({
  name: "SessionV1DetailPage",
  setup() {
    const params = Route.useParams();
    const search = Route.useSearch();
    const navigate = Route.useNavigate();
    const sessionsStore = useSessionsV1Store();
    const { sessions, sessionStateOverrides } = storeToRefs(sessionsStore);
    const remoteSession = shallowRef<SessionDetailResponse | null>(null);
    const optimisticWorking = shallowRef(false);
    const optimisticSessionState = shallowRef<{
      activityStatus?: string | null;
      lifecycleStatus?: string | null;
      retentionStatus?: string | null;
      sessionStatus?: string | null;
    } | null>(null);

    const selectedSession = computed(() => {
      return sessions.value.find((session) => session.session.id === params.value.id) ?? null;
    });

    const sessionStateOverride = computed(() => {
      return sessionStateOverrides.value[params.value.id] ?? null;
    });

    watch(
      () => params.value.id,
      async (sessionId, _previousSessionId, onCleanup) => {
        sessionsStore.setActiveSessionId(sessionId ?? null);
        remoteSession.value = null;

        if (!sessionId) {
          return;
        }

        const abortController = new AbortController();
        onCleanup(() => {
          abortController.abort();
        });

        try {
          const response = await apiFetch(`/api/sessions-v1/${encodeURIComponent(sessionId)}`, {
            signal: abortController.signal,
          });
          if (!response.ok || abortController.signal.aborted) {
            return;
          }

          const nextRemoteSession = normalizeSessionDetailResponse(await response.json());
          remoteSession.value = nextRemoteSession;

          const normalizedLifecycleStatus = normalizeLifecycleStatus(
            nextRemoteSession.lifecycleStatus ?? nextRemoteSession.status,
          ) ?? "running";
          const normalizedActivityStatus = nextRemoteSession.activityStatus === "busy"
            ? "busy"
            : "idle";

          const nextSession = {
            instanceId: nextRemoteSession.instanceId ?? search.value.instanceId ?? selectedSession.value?.instanceId ?? "",
            workspaceId: nextRemoteSession.workspaceId ?? selectedSession.value?.workspaceId ?? "",
            workspaceDirectory: nextRemoteSession.workspaceDirectory ?? selectedSession.value?.workspaceDirectory ?? "",
            workspaceDisplayName: nextRemoteSession.workspaceDisplayName ?? selectedSession.value?.workspaceDisplayName ?? null,
            isolationStrategy: nextRemoteSession.isolationStrategy ?? selectedSession.value?.isolationStrategy ?? "existing",
            sessionStatus: normalizedLifecycleStatus === "running"
              ? normalizedActivityStatus === "busy"
                ? "active"
                : "idle"
              : normalizedLifecycleStatus,
            session: {
              id: nextRemoteSession.id ?? sessionId,
              title: nextRemoteSession.title ?? selectedSession.value?.session.title ?? "Untitled session",
              time: selectedSession.value?.session.time ?? { created: 0, updated: 0 },
            },
            instanceStatus: selectedSession.value?.instanceStatus ?? "running",
            parentSessionId: selectedSession.value?.parentSessionId ?? null,
            sourceDirectory: nextRemoteSession.sourceDirectory ?? selectedSession.value?.sourceDirectory ?? null,
            branch: nextRemoteSession.branch ?? selectedSession.value?.branch ?? null,
            activityStatus: normalizedActivityStatus,
            lifecycleStatus: normalizedLifecycleStatus,
            retentionStatus: normalizeRetentionStatus(nextRemoteSession.retentionStatus),
            archivedAt: selectedSession.value?.archivedAt ?? null,
            typedInstanceStatus: selectedSession.value?.typedInstanceStatus ?? "running",
            isHidden: selectedSession.value?.isHidden ?? false,
            totalTokens: selectedSession.value?.totalTokens,
            totalCost: selectedSession.value?.totalCost,
            projectId: selectedSession.value?.projectId ?? null,
            projectName: selectedSession.value?.projectName ?? null,
            origin: nextRemoteSession.origin ?? selectedSession.value?.origin ?? null,
          } satisfies SessionListItem;

          sessionsStore.upsertSession(nextSession);
          dispatchSessionV1Upsert(nextSession);
        } catch (error) {
          if (error instanceof DOMException && error.name === "AbortError") {
            return;
          }
        }
      },
      { immediate: true },
    );

    const instanceId = computed<string | undefined>(() => {
      return search.value.instanceId
        ?? selectedSession.value?.instanceId
        ?? remoteSession.value?.instanceId
        ?? undefined;
    });

    const parentSession = computed(() => {
      const parentSessionId = search.value.parentSessionId ?? selectedSession.value?.parentSessionId ?? null;
      if (!parentSessionId) {
        return null;
      }

      return sessions.value.find((session) => session.session.id === parentSessionId) ?? null;
    });

    const parentSessionHref = computed(() => {
      const parentSessionId = search.value.parentSessionId ?? selectedSession.value?.parentSessionId ?? null;
      if (!parentSessionId) {
        return null;
      }

      if (!parentSession.value?.instanceId) {
        return `/sessions-v1/${parentSessionId}`;
      }

      return `/sessions-v1/${parentSessionId}?instanceId=${parentSession.value.instanceId}`;
    });

    const isDelegatedSession = computed(() => {
      return Boolean(search.value.parentSessionId || selectedSession.value?.parentSessionId);
    });

    const parentSessionLabel = computed(() => {
      return parentSession.value?.session.title?.trim() || "Parent session";
    });

    async function handleBackToParent(): Promise<void> {
      const parentSessionId = search.value.parentSessionId ?? selectedSession.value?.parentSessionId ?? null;
      if (!parentSessionId) {
        return;
      }

      if (parentSession.value?.instanceId) {
        await navigate({
          to: "/sessions-v1/$id",
          params: { id: parentSessionId },
          search: { instanceId: parentSession.value.instanceId, parentSessionId: undefined },
        });
        return;
      }

      await navigate({
        to: "/sessions-v1/$id",
        params: { id: parentSessionId },
        search: { instanceId: undefined, parentSessionId: undefined },
      });
    }

    const isArchived = computed(() => {
      return (
        optimisticSessionState.value?.retentionStatus
        ?? sessionStateOverride.value?.retentionStatus
        ?? selectedSession.value?.retentionStatus
        ?? remoteSession.value?.retentionStatus
      ) === "archived";
    });

    const isComposerDisabled = computed(() => {
      return isArchived.value || effectiveLifecycleStatus.value !== "running";
    });

    const { hasPendingPrompts, sentPrompts } = useSentPrompts(params.value.id);

    const effectiveLifecycleStatus = computed(() => {
      return normalizeLifecycleStatus(
        optimisticSessionState.value?.lifecycleStatus
        ?? optimisticSessionState.value?.sessionStatus
        ?? sessionStateOverride.value?.lifecycleStatus
        ?? sessionStateOverride.value?.sessionStatus
        ?? selectedSession.value?.lifecycleStatus
        ?? selectedSession.value?.sessionStatus
        ?? remoteSession.value?.lifecycleStatus
        ?? remoteSession.value?.status,
      ) ?? "running";
    });

    const effectiveActivityStatus = computed<"busy" | "idle">(() => {
      return optimisticWorking.value
        || sentPrompts.value.length > 0
        || hasPendingPrompts.value
        || optimisticSessionState.value?.activityStatus === "busy"
        || sessionStateOverride.value?.activityStatus === "busy"
        || (selectedSession.value?.activityStatus ?? remoteSession.value?.activityStatus) === "busy"
        ? "busy"
        : "idle";
    });

    watch(
      () => [selectedSession.value?.activityStatus, hasPendingPrompts.value, sentPrompts.value.length] as const,
      ([nextActivityStatus, nextHasPendingPrompts, nextSentPromptCount]) => {
        if (nextActivityStatus === "busy" || nextHasPendingPrompts || nextSentPromptCount > 0) {
          return;
        }

        optimisticWorking.value = false;
      },
      { immediate: true },
    );

    function handlePromptSent(): void {
      optimisticWorking.value = true;
      incrementPendingPrompts(params.value.id);
      sessionsStore.patchSession(params.value.id, {
        activityStatus: "busy",
        lifecycleStatus: "running",
        sessionStatus: "active",
      });
    }

    function handleSessionStateChanged(nextState: {
      activityStatus?: string | null;
      lifecycleStatus?: string | null;
      retentionStatus?: string | null;
      sessionStatus?: string | null;
    }): void {
      optimisticSessionState.value = {
        ...optimisticSessionState.value,
        ...nextState,
      };
    }

    return () => (
      <div
        style={{
          display: "flex",
          height: "100%",
          minHeight: 0,
          flexDirection: "column",
          overflow: "hidden",
        }}
      >
        <div
          style={{
            flexShrink: 0,
            padding: "0.75rem 1rem",
            display: "flex",
            flexDirection: "column",
            gap: "0.75rem",
          }}
        >
          <SessionDetailHeader
            id={params.value.id}
            instanceId={instanceId.value}
            origin={selectedSession.value?.origin ?? null}
            title={selectedSession.value?.session.title ?? remoteSession.value?.title}
            projectName={selectedSession.value?.projectName ?? null}
            activityStatus={effectiveActivityStatus.value}
            lifecycleStatus={effectiveLifecycleStatus.value}
            retentionStatus={optimisticSessionState.value?.retentionStatus ?? sessionStateOverride.value?.retentionStatus ?? selectedSession.value?.retentionStatus ?? remoteSession.value?.retentionStatus}
            sessionStateChanged={handleSessionStateChanged}
          />
          {isDelegatedSession.value ? (
            <div
              style={{
                display: "flex",
                flexWrap: "wrap",
                alignItems: "center",
                justifyContent: "space-between",
                gap: "0.75rem",
                border: "1px solid color-mix(in srgb, var(--border) 82%, transparent)",
                borderRadius: "0.875rem",
                background: "color-mix(in srgb, var(--muted) 32%, transparent)",
                padding: "0.75rem 0.875rem",
              }}
            >
              <div
                style={{
                  display: "flex",
                  minWidth: 0,
                  alignItems: "center",
                  gap: "0.75rem",
                }}
              >
                <div
                  style={{
                    display: "flex",
                    height: "2rem",
                    width: "2rem",
                    alignItems: "center",
                    justifyContent: "center",
                    borderRadius: "999px",
                    background: "color-mix(in srgb, var(--primary, #6366f1) 16%, transparent)",
                    color: "color-mix(in srgb, var(--primary, #6366f1) 70%, white 30%)",
                    flexShrink: 0,
                  }}
                >
                  <Bot size={16} aria-hidden="true" />
                </div>
                <div style={{ display: "flex", minWidth: 0, flexDirection: "column", gap: "0.125rem" }}>
                  <span
                    style={{
                      fontSize: "0.72rem",
                      fontWeight: 700,
                      letterSpacing: "0.08em",
                      textTransform: "uppercase",
                      color: "var(--muted-foreground, var(--muted))",
                    }}
                  >
                    Delegated subagent session
                  </span>
                  {parentSessionHref.value ? (
                    <a
                      href={parentSessionHref.value}
                      style={{
                        overflow: "hidden",
                        textOverflow: "ellipsis",
                        whiteSpace: "nowrap",
                        fontSize: "0.95rem",
                        fontWeight: 600,
                        color: "var(--foreground, var(--text))",
                        textDecoration: "none",
                      }}
                      title={parentSessionLabel.value}
                      onClick={(event) => {
                        event.preventDefault();
                        void handleBackToParent();
                      }}
                    >
                      {`From ${parentSessionLabel.value}`}
                    </a>
                  ) : (
                    <span
                      style={{
                        overflow: "hidden",
                        textOverflow: "ellipsis",
                        whiteSpace: "nowrap",
                        fontSize: "0.95rem",
                        fontWeight: 600,
                        color: "var(--foreground, var(--text))",
                      }}
                      title={parentSessionLabel.value}
                    >
                      Opened from a parent session
                    </span>
                  )}
                </div>
              </div>

              {search.value.parentSessionId || selectedSession.value?.parentSessionId ? (
                <button
                  type="button"
                  class="inline-flex h-8 items-center justify-center gap-2 rounded-md border bg-background px-3 text-sm font-medium shadow-xs transition-all hover:bg-accent hover:text-accent-foreground dark:border-input dark:bg-input/30 dark:hover:bg-input/50"
                  onClick={() => void handleBackToParent()}
                >
                  <ArrowLeft class="h-4 w-4" />
                  Back to parent
                </button>
              ) : null}
            </div>
          ) : null}
        </div>
        <ActivityStream key={`${params.value.id}-${instanceId.value}`} sessionId={params.value.id} instanceId={instanceId.value} />
        <Composer
          sessionId={params.value.id}
          instanceId={instanceId.value}
          disabled={isComposerDisabled.value}
          onPromptSent={handlePromptSent}
        />
        </div>
    );
  },
});

export const Route = createFileRoute("/sessions-v1_/$id")({
  validateSearch: (search: Record<string, unknown>) => ({
    instanceId: typeof search.instanceId === "string" ? search.instanceId : undefined,
    parentSessionId: typeof search.parentSessionId === "string" ? search.parentSessionId : undefined,
  }),
  component: SessionV1DetailPage,
});
