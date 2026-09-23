import type { DelegationDto } from "@/lib/client-types";

type DelegationEvent = {
  delegationId: string;
  parentToolCallId?: string | null;
  childSessionId?: string | null;
  title?: string;
  status?: DelegationDto["status"];
  createdAt?: string | null;
  background?: boolean;
};

export function applyDelegationCreated(
  prev: DelegationDto[],
  event: DelegationEvent,
): DelegationDto[] {
  const existingIndex = prev.findIndex((item) => item.delegationId === event.delegationId);
  const nextItem: DelegationDto = {
    delegationId: event.delegationId,
    parentToolCallId: event.parentToolCallId ?? null,
    childSessionId: event.childSessionId ?? null,
    title: event.title ?? "",
    status: event.status ?? "pending",
    createdAt: event.createdAt ?? null,
    ...(event.background ? { background: true } : {}),
  };

  if (existingIndex === -1) {
    return [...prev, nextItem];
  }

  const next = prev.slice();
  next[existingIndex] = { ...prev[existingIndex], ...nextItem };
  return next;
}

export function applyDelegationUpdated(
  prev: DelegationDto[],
  event: DelegationEvent,
): DelegationDto[] {
  const existingIndex = prev.findIndex((item) => item.delegationId === event.delegationId);
  if (existingIndex === -1) {
    return prev;
  }

  const existing = prev[existingIndex];
  const nextItem: DelegationDto = {
    ...existing,
    parentToolCallId: event.parentToolCallId ?? existing.parentToolCallId,
    childSessionId: event.childSessionId ?? existing.childSessionId,
    title: event.title ?? existing.title,
    status: event.status ?? existing.status,
    createdAt: event.createdAt ?? existing.createdAt ?? null,
    // Once in the background, a sub-agent stays there: an update that doesn't say so doesn't bring it back.
    ...(event.background || existing.background ? { background: true } : {}),
  };

  if (JSON.stringify(existing) === JSON.stringify(nextItem)) {
    return prev;
  }

  const next = prev.slice();
  next[existingIndex] = nextItem;
  return next;
}
