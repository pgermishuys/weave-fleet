/**
 * Client-only types — shapes constructed client-side, not REST API responses.
 * These types are built from WebSocket events or local state and cannot come
 * from OpenAPI generation.
 */

import type {
  SessionActivityStatus,
  SessionActionCapabilities,
  SessionLifecycleStatus,
  SessionRetentionStatus,
  InstanceStatus,
} from "@/lib/types";

// Re-export status types for consumer convenience
export type { SessionActivityStatus, SessionActionCapabilities, SessionLifecycleStatus, SessionRetentionStatus, InstanceStatus };

// ─── Streamed Event Model ──────────────────────────────────────────────────

/**
 * The simplified event model sent from the WebSocket to the browser.
 * Each event carries the raw SDK event type + properties for the client
 * to handle — we avoid mapping here to stay close to the SDK source of truth.
 */
export interface WebSocketEvent {
  type: string;
  eventId?: number | null;
  /** Deprecated compatibility alias for eventId during the migration. */
  sequenceNumber?: number | null;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  properties: Record<string, any>;
}

export interface CommittedSessionEvent {
  eventId?: number | null;
  /** Deprecated compatibility alias for eventId during the migration. */
  sequenceNumber?: number | null;
  topic: string;
  type: string;
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  payload: Record<string, any>;
  timestamp: number;
}

export interface DelegationDto {
  delegationId: string;
  parentToolCallId: string | null;
  childSessionId: string | null;
  title: string;
  status: "pending" | "running" | "completed" | "error" | "cancelled";
  createdAt?: string | null;
}

// ─── Accumulated Message (for useSessionEvents) ────────────────────────────

export interface AccumulatedTextPart {
  partId: string;
  type: "text";
  text: string;
}

export interface AccumulatedReasoningPart {
  partId: string;
  type: "reasoning";
  text: string;
  summary?: string;
}

export interface AccumulatedToolPart {
  partId: string;
  type: "tool";
  tool: string;
  callId: string;
  state: unknown;
}

export interface AccumulatedFilePart {
  partId: string;
  type: "file";
  mime: string;
  filename?: string;
  /** Full data URI or URL for rendering */
  url: string;
}

export type AccumulatedPart = AccumulatedTextPart | AccumulatedReasoningPart | AccumulatedToolPart | AccumulatedFilePart;

export interface AccumulatedMessage {
  messageId: string;
  sessionId: string;
  role: "user" | "assistant";
  parts: AccumulatedPart[];
  /** Cost in USD — populated from step-finish parts */
  cost?: number;
  tokens?: { input: number; output: number; reasoning: number };
  /** ISO timestamp */
  createdAt?: number;
  /** The agent name — sourced from info.agent for both user and assistant messages (v2) */
  agent?: string;
  modelID?: string;
  completedAt?: number;
  parentID?: string;
}

// ─── Image Attachment ───────────────────────────────────────────────────────

/** An image attachment sent alongside a prompt (base64-encoded). */
export interface ImageAttachment {
  /** MIME type: image/png, image/jpeg, image/gif, image/webp */
  mime: string;
  /** Optional filename for display */
  filename?: string;
  /** Base64-encoded image data (NOT the full data URI — just the base64 payload) */
  data: string;
}
