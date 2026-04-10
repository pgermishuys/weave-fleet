import type { DelegationDto } from "@/lib/api-types";

type DelegationEvent = {
  delegationId: string;
  parentToolCallId?: string | null;
  childSessionId?: string | null;
  title?: string;
  status?: DelegationDto["status"];
  createdAt?: string | null;
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
  };

  if (JSON.stringify(existing) === JSON.stringify(nextItem)) {
    return prev;
  }

  const next = prev.slice();
  next[existingIndex] = nextItem;
  return next;
}
