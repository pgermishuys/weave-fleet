
import { useEffect, useRef, useCallback, useState } from "react";
import type {
  AccumulatedMessage,
  DelegationDto,
  WebSocketEvent,
} from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";
import { fetchSessionStatus } from "@/lib/session-status-utils";
import {
  ensureMessage,
  mergeMessageUpdate,
  applyPartUpdate,
  applyTextDelta,
} from "@/lib/event-state";
import { applyDelegationCreated, applyDelegationUpdated } from "@/lib/delegation-state";
import { useMessagePagination } from "@/hooks/use-message-pagination";
import { prependMessages, convertFleetMessageToAccumulated } from "@/lib/pagination-utils";
import type { FleetMessage } from "@/lib/pagination-utils";
import { sessionCache } from "@/lib/session-cache";
import { useWeaveSocket, onReconnect } from "@/hooks/use-weave-socket";

export type SessionConnectionStatus =
  | "connecting"
  | "connected"
  | "recovering"
  | "disconnected"
  | "error"
  | "abandoned";

export interface UseSessionEventsResult {
  messages: AccumulatedMessage[];
  delegations: DelegationDto[];
  status: SessionConnectionStatus;
  sessionStatus: "idle" | "busy";
  error?: string;
  /** Imperatively transition sessionStatus to "idle" (e.g. after a successful abort). */
  forceIdle: () => void;
  /** Re-fetch messages from API and reload session status. */
  reconnect: () => void;
  /** Number of reconnection attempts since last successful connection. */
  reconnectAttempt: number;
  /** Whether there are older messages that can be loaded. */
  hasMoreMessages: boolean;
  /** Whether older messages are currently being fetched. */
  isLoadingOlder: boolean;
  /** Load the next older batch of messages. */
  loadOlderMessages: () => Promise<void>;
  /** Total number of messages in the session (null until first paginated load). */
  totalMessageCount: number | null;
  /** Error from the last failed older-messages fetch (null when no error). */
  loadOlderError: string | null;
  /**
   * Whether this mount was hydrated from the cache (true = skip auto-scroll,
   * restore saved scroll position instead).
   */
  cacheHit: boolean;
  /**
   * The scroll position to restore when cacheHit is true.
   * Null if the cache was invalidated (e.g. gap-fill fell back to full reload).
   */
  initialScrollPosition: { scrollTop: number; scrollHeight: number } | null;
  /**
   * Ref that the calling component (ActivityStreamV1) should update on every
   * scroll event so the current scroll position is available on unmount.
   */
  scrollPositionRef: React.MutableRefObject<{ scrollTop: number; scrollHeight: number } | null>;
}

/** Maximum messages held in memory — oldest are evicted when exceeded. */
const MAX_MESSAGES = 500;

export function useSessionEvents(
  sessionId: string,
  instanceId: string,
  onAgentSwitch?: (agent: string) => void,
  /**
   * Optional ref from useScrollAnchor's suppressAutoScroll. When provided,
   * it is set to `true` synchronously before hydrating cached messages so
   * that the messageCount auto-scroll effect is suppressed on the same render.
   */
  suppressAutoScrollRef?: React.MutableRefObject<boolean>,
): UseSessionEventsResult {
  const [messages, setMessages] = useState<AccumulatedMessage[]>([]);
  const [delegations, setDelegations] = useState<DelegationDto[]>([]);
  const [status, setStatus] = useState<SessionConnectionStatus>("connecting");
  const [sessionStatus, setSessionStatus] = useState<"idle" | "busy">("idle");
  const [error, setError] = useState<string | undefined>();

  const pagination = useMessagePagination();
  const {
    resetPagination,
    loadInitialMessages: paginationLoadInitial,
    loadOlderMessages: paginationLoadOlder,
    snapshotPagination,
    hydratePagination,
  } = pagination;

  const isMounted = useRef(false);
  // Keep onAgentSwitch in a ref to avoid stale closures
  const onAgentSwitchRef = useRef(onAgentSwitch);
  useEffect(() => { onAgentSwitchRef.current = onAgentSwitch; }, [onAgentSwitch]);
  // Track the last known message ID for gap-fill on reconnect
  const lastMessageIdRef = useRef<string | null>(null);
  // Cache hit state
  const [cacheHit, setCacheHit] = useState(false);
  const [initialScrollPosition, setInitialScrollPosition] = useState<{ scrollTop: number; scrollHeight: number } | null>(null);

  // Refs for cleanup closure (useState values are stale in cleanup)
  const messagesRef = useRef<AccumulatedMessage[]>(messages);
  const delegationsRef = useRef<DelegationDto[]>(delegations);
  const sessionStatusRef = useRef<"idle" | "busy">(sessionStatus);
  const snapshotPaginationRef = useRef(snapshotPagination);
  useEffect(() => {
    messagesRef.current = messages;
    delegationsRef.current = delegations;
    sessionStatusRef.current = sessionStatus;
    snapshotPaginationRef.current = snapshotPagination;
  }, [delegations, messages, sessionStatus, snapshotPagination]);

  // Scroll position written by ActivityStreamV1 on every scroll
  const scrollPositionRef = useRef<{ scrollTop: number; scrollHeight: number } | null>(null);

  // ── WebSocket ──────────────────────────────────────────────────────────────
  const { subscribe } = useWeaveSocket();

  // ── Message loading helpers ───────────────────────────────────────────────

  const loadAllMessages = useCallback(async (signal?: AbortSignal): Promise<void> => {
    if (!sessionId || !instanceId) return;
    if (isMounted.current) {
      setCacheHit(false);
      setInitialScrollPosition(null);
    }
    try {
      const url = `/api/sessions/${encodeURIComponent(sessionId)}/messages`;
      const response = await apiFetch(url, signal ? { signal } : undefined);
      if (!response.ok) return;
      const data = await response.json() as { messages?: FleetMessage[] };
      if (!data.messages?.length) return;
      const accumulated = data.messages.map(convertFleetMessageToAccumulated);
      if (!signal?.aborted) {
        setMessages(
          accumulated.length > MAX_MESSAGES
            ? accumulated.slice(accumulated.length - MAX_MESSAGES)
            : accumulated
        );
      }
      resetPagination();
    } catch (err) {
      // Abort is expected when session changes — not an error.
      if (err instanceof DOMException && err.name === "AbortError") return;
      // best-effort
    }
  }, [sessionId, instanceId, resetPagination]);

  const loadMessagesSince = useCallback(async (afterId: string | null, signal?: AbortSignal): Promise<void> => {
    if (!sessionId || !instanceId) return;
    if (!afterId) return loadAllMessages(signal);
    try {
      const url = `/api/sessions/${encodeURIComponent(sessionId)}/messages?instanceId=${encodeURIComponent(instanceId)}&after=${encodeURIComponent(afterId)}`;
      const response = await apiFetch(url, signal ? { signal } : undefined);
      if (!response.ok) {
        // Gap-fill failed — preserve the current scroll position so the full
        // reload (which clears initialScrollPosition) doesn't lose the user's place.
        const savedScroll = scrollPositionRef.current;
        await loadAllMessages(signal);
        if (savedScroll && isMounted.current && !signal?.aborted) {
          setInitialScrollPosition(savedScroll);
        }
        return;
      }
      const data = await response.json() as { messages?: FleetMessage[] };
      if (!data.messages?.length) return;
      if (signal?.aborted) return;
      const accumulated = data.messages.map(convertFleetMessageToAccumulated);
      setMessages(prev => {
        const incomingById = new Map(accumulated.map((m: AccumulatedMessage) => [m.messageId, m]));
        // Update existing messages that have richer content in the incoming data,
        // and append any genuinely new messages.
        const updated = prev.map((existing: AccumulatedMessage) => {
          const incoming = incomingById.get(existing.messageId);
          if (!incoming) return existing;
          incomingById.delete(existing.messageId);
          // If the existing message has no renderable parts but the incoming one does, use the incoming version.
          const existingHasContent = existing.parts.some(
            (p) => (p.type === "text" && p.text.trim().length > 0) || p.type === "tool" || p.type === "file"
          );
          if (!existingHasContent && incoming.parts.length > 0) return incoming;
          return existing;
        });
        const newMessages = Array.from(incomingById.values());
        const merged = [...updated, ...newMessages];
        return merged.length > MAX_MESSAGES ? merged.slice(merged.length - MAX_MESSAGES) : merged;
      });
    } catch (err) {
      if (err instanceof DOMException && err.name === "AbortError") return;
      const savedScroll = scrollPositionRef.current;
      await loadAllMessages(signal);
      if (savedScroll && isMounted.current && !signal?.aborted) {
        setInitialScrollPosition(savedScroll);
      }
    }
  }, [sessionId, instanceId, loadAllMessages]);

  const loadInitialMessages = useCallback(async (signal?: AbortSignal): Promise<void> => {
    if (!sessionId || !instanceId) return;
    try {
      const accumulated = await paginationLoadInitial(sessionId, instanceId, signal);
      if (accumulated.length > 0 && !signal?.aborted) setMessages(accumulated);
    } catch (err) {
      if (err instanceof DOMException && err.name === "AbortError") return;
      // best-effort
    }
  }, [sessionId, instanceId, paginationLoadInitial]);

  const loadDelegations = useCallback(async (signal?: AbortSignal): Promise<void> => {
    if (!sessionId || !instanceId) return;
    try {
      const response = await apiFetch(
        `/api/sessions/${encodeURIComponent(sessionId)}/delegations`,
        signal ? { signal } : undefined,
      );
      if (!response.ok) return;

      const data = await response.json() as DelegationDto[];
      if (!signal?.aborted) {
        setDelegations(Array.isArray(data) ? data : []);
      }
    } catch (err) {
      if (err instanceof DOMException && err.name === "AbortError") return;
    }
  }, [instanceId, sessionId]);

  const loadSessionStatus = useCallback(async (signal?: AbortSignal): Promise<void> => {
    if (!sessionId || !instanceId) return;
    const s = await fetchSessionStatus(sessionId, instanceId);
    if (isMounted.current && !signal?.aborted) setSessionStatus(s);
  }, [sessionId, instanceId]);

  // ── Mount: load initial data, subscribe to WS topic ───────────────────────

  useEffect(() => {
    isMounted.current = true;

    // AbortController: cancel in-flight API requests when the session changes
    // so that stale responses from the previous session cannot overwrite the
    // current session's messages.
    const abortController = new AbortController();
    const { signal } = abortController;

    // Reset state when sessionId changes to prevent stale messages from the
    // previous session leaking into the new one.  Without this, navigating
    // from a session with messages to a brand-new (empty) session keeps the
    // old messages visible because loadInitialMessages() only calls
    // setMessages when there are messages to show.
    setMessages([]);
    setDelegations([]);
    setSessionStatus("idle");
    setStatus("connecting");
    setError(undefined);
    lastMessageIdRef.current = null;

    const cached = sessionCache.get(sessionId, instanceId);
    if (cached) {
      // Cache hit: hydrate instantly, then gap-fill
      if (suppressAutoScrollRef) suppressAutoScrollRef.current = true;
      setMessages(cached.messages);
      setDelegations(cached.delegations);
      setSessionStatus(cached.sessionStatus);
      lastMessageIdRef.current = cached.lastMessageId;
      hydratePagination(cached.pagination);
      setCacheHit(true);
      setInitialScrollPosition({ scrollTop: cached.scrollPosition, scrollHeight: cached.scrollHeight });
      void Promise.all([loadMessagesSince(cached.lastMessageId, signal), loadSessionStatus(signal), loadDelegations(signal)])
        .then(() => {
          if (isMounted.current && !signal.aborted) setStatus("connected");
        })
        .catch(() => {
          if (isMounted.current && !signal.aborted) setStatus("connected");
        });
    } else {
      void Promise.all([loadInitialMessages(signal), loadSessionStatus(signal), loadDelegations(signal)])
        .then(() => {
          if (isMounted.current && !signal.aborted) setStatus("connected");
        })
        .catch(() => {
          // Transition to connected even on error so the UI isn't stuck on
          // the "Connecting…" spinner forever.
          if (isMounted.current && !signal.aborted) setStatus("connected");
        });
    }

    // Subscribe to session topic on the shared WebSocket
    const topic = `session:${sessionId}`;
    const unsub = subscribe([topic], (_topic: string, rawData: unknown) => {
      if (!isMounted.current) return;
      let event: WebSocketEvent;
      try {
        event = rawData as WebSocketEvent;
      } catch {
        return;
      }
      handleEvent(event, sessionId, setMessages, setDelegations, setStatus, setSessionStatus, setError, onAgentSwitchRef, lastMessageIdRef);
    });

    // On WebSocket reconnect, gap-fill from the last known message so that
    // messages missed during the disconnection are fetched from the server.
    const unsubReconnect = onReconnect(() => {
      if (!isMounted.current) return;
      void Promise.all([
        loadMessagesSince(lastMessageIdRef.current),
        loadDelegations(),
        loadSessionStatus(),
      ]);
    });

    return () => {
      isMounted.current = false;
      // Abort any in-flight API requests to prevent stale responses from
      // the previous session from overwriting the new session's messages.
      abortController.abort();
      unsub();
      unsubReconnect();

      // Save to cache on unmount if we have messages
      if (messagesRef.current.length > 0) {
        const scrollPos = scrollPositionRef.current;
        sessionCache.set(sessionId, instanceId, {
          messages: messagesRef.current,
          delegations: delegationsRef.current,
          scrollPosition: scrollPos?.scrollTop ?? 0,
          scrollHeight: scrollPos?.scrollHeight ?? 0,
          sessionStatus: sessionStatusRef.current,
          lastMessageId: lastMessageIdRef.current,
          pagination: snapshotPaginationRef.current(),
          timestamp: Date.now(),
        });
      }
    };
  // eslint-disable-next-line react-hooks/exhaustive-deps -- intentionally stable: runs once per session/instance pair
  }, [sessionId, instanceId]);

  const forceIdle = useCallback(() => setSessionStatus("idle"), []);

  const loadOlderMessages = useCallback(async () => {
    if (!sessionId || !instanceId) return;
    const older = await paginationLoadOlder(sessionId, instanceId);
    if (older.length > 0) {
      setMessages((prev: AccumulatedMessage[]) => prependMessages(prev, older));
    }
  }, [sessionId, instanceId, paginationLoadOlder]);

  // Manual reconnect: re-fetch messages and session status from API
  const reconnect = useCallback(() => {
    setStatus("recovering");
    void Promise.all([
      loadMessagesSince(lastMessageIdRef.current),
      loadDelegations(),
      loadSessionStatus(),
    ]).then(() => {
      if (isMounted.current) {
        setStatus("connected");
        setError(undefined);
      }
    });
  }, [loadDelegations, loadMessagesSince, loadSessionStatus]);

  return {
    messages,
    delegations,
    status,
    sessionStatus,
    error,
    forceIdle,
    reconnect,
    reconnectAttempt: 0,
    hasMoreMessages: pagination.hasMore,
    isLoadingOlder: pagination.isLoadingOlder,
    loadOlderMessages,
    totalMessageCount: pagination.totalCount,
    loadOlderError: pagination.loadError,
    cacheHit,
    initialScrollPosition,
    scrollPositionRef,
  };
}

// ─── Event handler (pure — receives setters to avoid stale closures) ──────────

type SetMessages = React.Dispatch<React.SetStateAction<AccumulatedMessage[]>>;
type SetDelegations = React.Dispatch<React.SetStateAction<DelegationDto[]>>;
type SetStatus = React.Dispatch<React.SetStateAction<SessionConnectionStatus>>;
type SetSessionStatus = React.Dispatch<React.SetStateAction<"idle" | "busy">>;
type SetError = React.Dispatch<React.SetStateAction<string | undefined>>;

export function handleEvent(
  event: WebSocketEvent,
  sessionId: string,
  setMessages: SetMessages,
  setDelegations: SetDelegations,
  setStatus: SetStatus,
  setSessionStatus: SetSessionStatus,
  setError: SetError,
  onAgentSwitchRef: React.MutableRefObject<((agent: string) => void) | undefined>,
  lastMessageIdRef: React.MutableRefObject<string | null>,
): void {
  const { type, properties } = event;
  const delegationId = properties?.delegationId ?? properties?.DelegationId;
  const parentToolCallId = properties?.parentToolCallId ?? properties?.ParentToolCallId;
  const childSessionId = properties?.childSessionId ?? properties?.ChildSessionId;
  const delegationTitle = properties?.title ?? properties?.Title;
  const delegationStatus = properties?.status ?? properties?.Status;
  const delegationCreatedAt = properties?.createdAt ?? properties?.CreatedAt;

  if (type === "server.connected") {
    setStatus("connected");
    return;
  }

  if (type === "error") {
    setError(properties?.message ?? "Unknown error");
    setStatus("error");
    return;
  }

  if (type === "session.status") {
    const statusType = properties?.status?.type;
    if (statusType === "idle") setSessionStatus("idle");
    else if (statusType === "busy") setSessionStatus("busy");
    return;
  }

  if (type === "session.idle") {
    setSessionStatus("idle");
    return;
  }

  if (type === "message.updated") {
    const info = properties?.info;
    if (!info?.id) return;
    lastMessageIdRef.current = info.id;
    setMessages((prev) => {
      const next = mergeMessageUpdate(ensureMessage(prev, info), info);
      if (next.length > MAX_MESSAGES) {
        return next.slice(next.length - MAX_MESSAGES);
      }
      return next;
    });
    return;
  }

  if (type === "delegation.created") {
    if (!delegationId) return;
    setDelegations((prev) => applyDelegationCreated(prev, {
      delegationId,
      parentToolCallId,
      childSessionId,
      title: delegationTitle,
      status: delegationStatus,
      createdAt: delegationCreatedAt,
    }));
    return;
  }

  if (type === "delegation.updated") {
    if (!delegationId) return;
    setDelegations((prev) => applyDelegationUpdated(prev, {
      delegationId,
      parentToolCallId,
      childSessionId,
      title: delegationTitle,
      status: delegationStatus,
      createdAt: delegationCreatedAt,
    }));
    return;
  }

  if (type === "message.part.updated") {
    const part = properties?.part;
    if (!part?.messageID) return;
    setMessages((prev) => applyPartUpdate(prev, { ...part, sessionID: sessionId }));

    if (part.type === "tool" && part.state?.status === "completed") {
      if (part.tool === "plan_exit") {
        onAgentSwitchRef.current?.("build");
      } else if (part.tool === "plan_enter") {
        onAgentSwitchRef.current?.("plan");
      }
    }
    return;
  }

  if (type === "message.part.delta") {
    const { messageID, partID, field, delta } = properties ?? {};
    if (field !== "text" || !messageID || !partID) return;
    setMessages((prev) =>
      applyTextDelta(prev, messageID, partID, sessionId, delta ?? "")
    );
    return;
  }
}
