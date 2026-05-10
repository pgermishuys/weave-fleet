import type { SessionListItem } from "@/lib/api-types";

const SESSION_V1_SYNC_EVENT = "weave:session-v1-sync";

type SessionV1SyncOperation =
  | {
    type: "remove";
    sessionId: string;
  }
  | {
    type: "upsert";
    session: SessionListItem;
  };

export function dispatchSessionV1Removed(sessionId: string): void {
  dispatchSessionV1SyncEvent({ type: "remove", sessionId });
}

export function dispatchSessionV1Upsert(session: SessionListItem): void {
  dispatchSessionV1SyncEvent({ type: "upsert", session });
}

export function addSessionV1SyncListener(listener: (operation: SessionV1SyncOperation) => void): () => void {
  if (typeof window === "undefined") {
    return () => undefined;
  }

  const handleEvent = (event: Event) => {
    const customEvent = event as CustomEvent<SessionV1SyncOperation>;
    if (!customEvent.detail) {
      return;
    }

    listener(customEvent.detail);
  };

  window.addEventListener(SESSION_V1_SYNC_EVENT, handleEvent);
  return () => {
    window.removeEventListener(SESSION_V1_SYNC_EVENT, handleEvent);
  };
}

function dispatchSessionV1SyncEvent(operation: SessionV1SyncOperation): void {
  if (typeof window === "undefined") {
    return;
  }

  window.dispatchEvent(new CustomEvent<SessionV1SyncOperation>(SESSION_V1_SYNC_EVENT, {
    detail: operation,
  }));
}
