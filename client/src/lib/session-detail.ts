/**
 * The session detail the server sends (`GET /api/sessions/{id}`), tolerant of old shapes: camelCase or PascalCase
 * keys, nulls, wrong types. Pure functions, no state; `useSessionDetail` fetches and files the result.
 */
import type { SessionActionCapabilities, SessionListItem, SessionOrigin } from "@/api/client";
import type { SessionActivityStatus } from "@/lib/types";

export function normalizeRetentionStatus(value: string | null | undefined): "active" | "archived" {
  return value === "archived" ? "archived" : "active";
}

export interface SessionDetailResponse {
  id?: string | null;
  instanceId?: string | null;
  parentSessionId?: string | null;
  workspaceId?: string | null;
  workspaceDirectory?: string | null;
  workspaceDisplayName?: string | null;
  sourceDirectory?: string | null;
  isolationStrategy?: string | null;
  branch?: string | null;
  title?: string | null;
  createdAt?: string | null;
  projectId?: string | null;
  projectName?: string | null;
  status?: string | null;
  activityStatus?: string | null;
  lifecycleStatus?: string | null;
  retentionStatus?: string | null;
  totalTokens?: number | null;
  totalCost?: number | null;
  capabilities?: SessionActionCapabilities;
  origin?: SessionOrigin | null;
  harnessType?: string | null;
  /** The profile the session started with, if it has one. */
  harnessProfileName?: string | null;
  tags?: string[];
  forkedFromSessionId?: string | null;
  spawnedBySessionId?: string | null;
  spawnKind?: string | null;
  lineageDetachedAt?: string | null;
  /** While the harness waits to retry a failed model call: which attempt, out of how many, why and when. */
  retryAttempt?: number | null;
  retryMaxAttempts?: number | null;
  retryMessage?: string | null;
  retryNext?: string | null;
}

function getStringField(
  value: Record<string, unknown>,
  camelKey: string,
  pascalKey: string,
): string | null | undefined {
  const candidate = value[camelKey] ?? value[pascalKey];
  return typeof candidate === "string" ? candidate : candidate == null ? null : undefined;
}

function getNumberField(
  value: Record<string, unknown>,
  camelKey: string,
  pascalKey: string,
): number | null | undefined {
  const candidate = value[camelKey] ?? value[pascalKey];
  return typeof candidate === "number" ? candidate : candidate == null ? null : undefined;
}

export function normalizeSessionDetailResponse(payload: unknown): SessionDetailResponse {
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
    parentSessionId: getStringField(value, "parentSessionId", "ParentSessionId"),
    forkedFromSessionId: getStringField(value, "forkedFromSessionId", "ForkedFromSessionId"),
    spawnedBySessionId: getStringField(value, "spawnedBySessionId", "SpawnedBySessionId"),
    spawnKind: getStringField(value, "spawnKind", "SpawnKind"),
    workspaceId: getStringField(value, "workspaceId", "WorkspaceId"),
    workspaceDirectory: getStringField(value, "workspaceDirectory", "WorkspaceDirectory"),
    workspaceDisplayName: getStringField(value, "workspaceDisplayName", "WorkspaceDisplayName"),
    sourceDirectory: getStringField(value, "sourceDirectory", "SourceDirectory"),
    isolationStrategy: getStringField(value, "isolationStrategy", "IsolationStrategy"),
    branch: getStringField(value, "branch", "Branch"),
    title: getStringField(value, "title", "Title"),
    createdAt: getStringField(value, "createdAt", "CreatedAt"),
    projectId: getStringField(value, "projectId", "ProjectId"),
    projectName: getStringField(value, "projectName", "ProjectName"),
    status: getStringField(value, "status", "Status"),
    activityStatus: getStringField(value, "activityStatus", "ActivityStatus"),
    lifecycleStatus: getStringField(value, "lifecycleStatus", "LifecycleStatus"),
    retentionStatus: getStringField(value, "retentionStatus", "RetentionStatus"),
    totalTokens: getNumberField(value, "totalTokens", "TotalTokens"),
    totalCost: getNumberField(value, "totalCost", "TotalCost"),
    capabilities: (value.capabilities ?? value.Capabilities) as SessionActionCapabilities | undefined,
    origin,
    harnessType: getStringField(value, "harnessType", "HarnessType"),
    harnessProfileName: getStringField(value, "harnessProfileName", "HarnessProfileName"),
    tags: Array.isArray(value.tags ?? value.Tags) ? (value.tags ?? value.Tags) as string[] : undefined,
    retryAttempt: getNumberField(value, "retryAttempt", "RetryAttempt"),
    retryMaxAttempts: getNumberField(value, "retryMaxAttempts", "RetryMaxAttempts"),
    retryMessage: getStringField(value, "retryMessage", "RetryMessage"),
    retryNext: getStringField(value, "retryNext", "RetryNext"),
  };
}

export function sessionTimeFromCreatedAt(createdAt: string | null | undefined): { created: number; updated: number } {
  const createdMs = createdAt ? Date.parse(createdAt) : Number.NaN;
  const created = Number.isFinite(createdMs) ? createdMs : Date.now();
  return { created, updated: created };
}

export function normalizeLifecycleStatus(value: string | null | undefined): "running" | "completed" | "stopped" | "error" | "disconnected" | null {
  switch (value) {
    case "active":
    case "delegating":
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

export function normalizeActivityStatus(value: string | null | undefined): SessionActivityStatus | null {
  switch (value) {
    case "active":
    case "busy":
      return "busy";
    case "delegating":
      return "delegating";
    case "retry":
      return "retry";
    case "waiting_input":
      return "waiting_input";
    case "idle":
      return "idle";
    default:
      return null;
  }
}

// A retrying session is waiting out a model error mid-turn: still working, never idle.
export function isActiveActivityStatus(value: string | null | undefined): value is "busy" | "delegating" | "retry" {
  return value === "busy" || value === "delegating" || value === "retry";
}

export function isDiffStalingStatus(
  activityStatus: SessionActivityStatus | null | undefined,
  lifecycleStatus: string | null | undefined,
): boolean {
  return isActiveActivityStatus(activityStatus) || lifecycleStatus === "running" && activityStatus === "waiting_input";
}


/**
 * The store row for a freshly fetched detail. What the server sent wins; what it left out comes from the row the
 * list already holds (`existing`), then from `searchInstanceId`, then from a default.
 */
export function buildSessionListItem(
  sessionId: string,
  detail: SessionDetailResponse,
  existing: SessionListItem | null | undefined,
  searchInstanceId: string | undefined,
): SessionListItem {
  const selectedSession = existing;
  const nextRemoteSession = detail;
  const normalizedLifecycleStatus = normalizeLifecycleStatus(
    nextRemoteSession.lifecycleStatus ?? nextRemoteSession.status,
  ) ?? "running";
  const normalizedActivityStatus = normalizeActivityStatus(nextRemoteSession.activityStatus) ?? "idle";

  return {
    instanceId: nextRemoteSession.instanceId ?? searchInstanceId ?? selectedSession?.instanceId ?? "",
    workspaceId: nextRemoteSession.workspaceId ?? selectedSession?.workspaceId ?? "",
    workspaceDirectory: nextRemoteSession.workspaceDirectory ?? selectedSession?.workspaceDirectory ?? "",
    workspaceDisplayName: nextRemoteSession.workspaceDisplayName ?? selectedSession?.workspaceDisplayName ?? null,
    isolationStrategy: nextRemoteSession.isolationStrategy ?? selectedSession?.isolationStrategy ?? "existing",
    sessionStatus: normalizedLifecycleStatus === "running"
      ? normalizedActivityStatus === "waiting_input"
        ? "waiting_input"
        : isActiveActivityStatus(normalizedActivityStatus)
        ? "active"
        : "idle"
      : normalizedLifecycleStatus,
    session: {
      id: nextRemoteSession.id ?? sessionId,
      title: nextRemoteSession.title ?? selectedSession?.session.title ?? "Untitled session",
      // A session opened before the list has it (e.g. just created) needs its real age,
      // not the epoch, or the sidebar shows it as decades old.
      time: selectedSession?.session.time ?? sessionTimeFromCreatedAt(nextRemoteSession.createdAt),
      tags: nextRemoteSession.tags ?? selectedSession?.session.tags ?? [],
    },
    instanceStatus: selectedSession?.instanceStatus ?? "running",
    parentSessionId: nextRemoteSession.parentSessionId ?? selectedSession?.parentSessionId ?? null,
    forkedFromSessionId: nextRemoteSession.forkedFromSessionId ?? selectedSession?.forkedFromSessionId ?? null,
    spawnedBySessionId: nextRemoteSession.spawnedBySessionId ?? selectedSession?.spawnedBySessionId ?? null,
    spawnKind: nextRemoteSession.spawnKind ?? selectedSession?.spawnKind ?? null,
    sourceDirectory: nextRemoteSession.sourceDirectory ?? selectedSession?.sourceDirectory ?? null,
    branch: nextRemoteSession.branch ?? selectedSession?.branch ?? null,
    activityStatus: normalizedActivityStatus,
    lifecycleStatus: normalizedLifecycleStatus,
    retentionStatus: normalizeRetentionStatus(nextRemoteSession.retentionStatus),
    archivedAt: selectedSession?.archivedAt ?? null,
    typedInstanceStatus: selectedSession?.typedInstanceStatus ?? "running",
    isHidden: selectedSession?.isHidden ?? false,
    totalTokens: nextRemoteSession.totalTokens ?? selectedSession?.totalTokens,
    totalCost: nextRemoteSession.totalCost ?? selectedSession?.totalCost,
    // The server's project wins: a new session lands in Scratch, not "Ungrouped".
    projectId: nextRemoteSession.projectId ?? selectedSession?.projectId ?? null,
    projectName: nextRemoteSession.projectName ?? selectedSession?.projectName ?? null,
    capabilities: nextRemoteSession.capabilities ?? selectedSession?.capabilities,
    origin: nextRemoteSession.origin ?? selectedSession?.origin ?? null,
    // The server always sends it; an empty one finds no harness, so nothing harness-specific is offered.
    harnessType: nextRemoteSession.harnessType ?? selectedSession?.harnessType ?? "",
    tags: nextRemoteSession.tags ?? selectedSession?.tags ?? [],
    // Which attempt a retrying session is on, why and when, so a page opened mid-retry says so.
    ...(normalizedActivityStatus === "retry"
      ? {
          retryAttempt: nextRemoteSession.retryAttempt ?? selectedSession?.retryAttempt ?? null,
          retryMaxAttempts: nextRemoteSession.retryMaxAttempts ?? selectedSession?.retryMaxAttempts ?? null,
          retryMessage: nextRemoteSession.retryMessage ?? selectedSession?.retryMessage ?? null,
          retryNext: nextRemoteSession.retryNext ?? selectedSession?.retryNext ?? null,
        }
      : {}),
  } satisfies SessionListItem;
}
