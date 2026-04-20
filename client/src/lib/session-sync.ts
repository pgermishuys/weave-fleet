import type { SessionListItem } from "@/lib/api-types";

const SESSION_SYNC_EVENT = "weave:session-sync";

type SessionSyncOperation =
  | {
    type: "remove";
    sessionId: string;
  }
  | {
    type: "upsert";
    session: SessionListItem;
  };

export function dispatchSessionRemoved(sessionId: string): void {
  dispatchSessionSyncEvent({ type: "remove", sessionId });
}

export function dispatchSessionUpsert(session: SessionListItem): void {
  dispatchSessionSyncEvent({ type: "upsert", session });
}

export function addSessionSyncListener(listener: (operation: SessionSyncOperation) => void): () => void {
  if (typeof window === "undefined") {
    return () => undefined;
  }

  const handleEvent = (event: Event) => {
    const customEvent = event as CustomEvent<SessionSyncOperation>;
    if (!customEvent.detail) {
      return;
    }

    listener(customEvent.detail);
  };

  window.addEventListener(SESSION_SYNC_EVENT, handleEvent);
  return () => {
    window.removeEventListener(SESSION_SYNC_EVENT, handleEvent);
  };
}

function dispatchSessionSyncEvent(operation: SessionSyncOperation): void {
  if (typeof window === "undefined") {
    return;
  }

  window.dispatchEvent(new CustomEvent<SessionSyncOperation>(SESSION_SYNC_EVENT, {
    detail: operation,
  }));
}
