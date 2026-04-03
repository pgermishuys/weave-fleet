"use client";

import { useState, useCallback, useEffect, useRef } from "react";

export interface QueuedMessage {
  id: string;
  text: string;
  agent?: string;
  model?: { providerID: string; modelID: string };
}

export interface UseMessageQueueResult {
  /** Messages waiting to be sent. */
  queue: QueuedMessage[];
  /** Add a message to the end of the queue. */
  enqueue: (text: string, agent?: string, model?: { providerID: string; modelID: string }) => void;
  /** Remove a message at a specific index. */
  removeAt: (index: number) => void;
  /** Clear all queued messages. */
  clear: () => void;
  /** Whether the queue is currently auto-sending the next message. */
  isAutoSending: boolean;
}

let nextQueueId = 0;

/**
 * Hook to manage a follow-up message queue.
 *
 * When the session is busy, messages are enqueued. When the session
 * transitions to idle, the next queued message is auto-sent.
 *
 * @param sessionStatus - Current session status ("idle" | "busy")
 * @param onSend - Callback to send a message (same signature as PromptInput.onSend)
 */
export function useMessageQueue(
  sessionStatus: "idle" | "busy",
  onSend?: (text: string, agent?: string, model?: { providerID: string; modelID: string }) => Promise<void>
): UseMessageQueueResult {
  const [queue, setQueue] = useState<QueuedMessage[]>([]);
  const [isAutoSending, setIsAutoSending] = useState(false);
  const onSendRef = useRef(onSend);
  onSendRef.current = onSend;

  // Track previous session status to detect idle transitions
  const prevStatusRef = useRef(sessionStatus);

  const enqueue = useCallback(
    (text: string, agent?: string, model?: { providerID: string; modelID: string }) => {
      const id = `queue-${++nextQueueId}`;
      setQueue((prev) => [...prev, { id, text, agent, model }]);
    },
    []
  );

  const removeAt = useCallback((index: number) => {
    setQueue((prev) => prev.filter((_, i) => i !== index));
  }, []);

  const clear = useCallback(() => {
    setQueue([]);
  }, []);

  // Auto-send: when session transitions from busy → idle and queue is non-empty
  useEffect(() => {
    const wasBusy = prevStatusRef.current === "busy";
    prevStatusRef.current = sessionStatus;

    if (!wasBusy || sessionStatus !== "idle") return;
    if (queue.length === 0) return;
    if (!onSendRef.current) return;

    const next = queue[0];
    setQueue((prev) => prev.slice(1));
    setIsAutoSending(true);

    void onSendRef.current(next.text, next.agent, next.model).finally(() => {
      setIsAutoSending(false);
    });
  }, [sessionStatus, queue]);

  return { queue, enqueue, removeAt, clear, isAutoSending };
}
