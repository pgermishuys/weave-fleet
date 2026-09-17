export interface EventCursorMetadata {
  eventId?: number | null;
}

export type JsonValue = JsonPrimitive | JsonObject | JsonValue[];

export type JsonPrimitive = string | number | boolean | null;

export interface JsonObject {
  [key: string]: JsonValue;
}

export interface SessionStartedPayload {
  sessionId: string;
  instanceId: string | null;
  workspaceId: string | null;
  title: string | null;
  projectId: string | null;
  parentSessionId: string | null;
  isHidden: boolean | null;
}

export interface SessionIdledPayload {
  sessionId: string;
}

export interface SessionDeletedPayload {
  sessionId: string;
}

export interface SessionArchivedPayload {
  sessionId: string;
  archivedAt: string;
}

export interface TurnStartedPayload {
  sessionID: string;
  messageID: string;
  index: number;
  agent: string | null;
  modelID: string | null;
  parentID: string | null;
}

export interface TurnTokenUsage {
  input: number;
  output: number;
  reasoning: number;
}

export interface TurnEndedPayload {
  sessionID: string;
  messageID: string;
  index: number;
  reason: string | null;
  cost: number;
  tokens: TurnTokenUsage | null;
  completedAt: number | null;
}

/** A failure the harness reported, normalised server-side so no harness shapes reach the client. */
export interface TurnError {
  name: string;
  message: string;
  isRetryable: boolean;
}

export interface TurnFailedPayload {
  sessionID: string;
  messageID: string | null;
  error: TurnError;
}

export interface MessageEventTime {
  created: number;
  completed: number | null;
}

export interface MessageTokenUsage {
  input: number;
  output: number;
  reasoning: number;
}

export interface MessageEventInfo {
  id: string;
  role: string;
  sessionID: string;
  agent: string | null;
  modelID: string | null;
  parentID: string | null;
  time: MessageEventTime;
  cost: number | null;
  tokens: MessageTokenUsage | null;
  /** Set when the turn this message belongs to failed. Named `turnError` because the harness puts its
   * own differently shaped `error` on the message; Fleet normalises that into this one. */
  turnError?: TurnError | null;
  /** Why the model stopped (e.g. "stop", "length", "error"), when the harness reports it. */
  finish?: string | null;
}

export interface BaseMessageEventPart {
  id: string;
  sessionID: string;
  messageID: string;
}

export interface TextMessageEventPart extends BaseMessageEventPart {
  type: "text";
  text: string;
}

export interface ReasoningMessageEventPart extends BaseMessageEventPart {
  type: "reasoning";
  text: string;
  summary: string | null;
}

export interface ToolPendingState {
  status: "pending";
  input: JsonValue | null;
}

export interface ToolRunningState {
  status: "running";
  input: JsonValue | null;
}

export interface ToolCompletedState {
  status: "completed";
  input: JsonValue | null;
  output: JsonValue | null;
  metadata: JsonValue | null;
  title?: string | null;
}

export interface ToolErrorState {
  status: "error";
  input: JsonValue | null;
  output: JsonValue | null;
  error?: string | null;
  metadata?: JsonValue | null;
}

export interface ToolCancelledState {
  status: "cancelled";
  input: JsonValue | null;
}

export type ToolInvocationState =
  | ToolPendingState
  | ToolRunningState
  | ToolCompletedState
  | ToolErrorState
  | ToolCancelledState;

export interface ToolMessageEventPart extends BaseMessageEventPart {
  type: "tool";
  tool: string;
  callID: string;
  state: ToolInvocationState;
}

export interface FileMessageEventPart extends BaseMessageEventPart {
  type: "file";
  mime: string;
  url: string;
  filename: string | null;
}

export interface StepStartedMessageEventPart extends BaseMessageEventPart {
  type: "step-start";
  index: number;
}

export interface StepFinishedMessageEventPart extends BaseMessageEventPart {
  type: "step-finish";
  index: number;
  reason: string | null;
  cost: number;
  tokens: MessageTokenUsage | null;
  completedAt: number | null;
}

export type MessageEventPart =
  | TextMessageEventPart
  | ReasoningMessageEventPart
  | ToolMessageEventPart
  | FileMessageEventPart
  | StepStartedMessageEventPart
  | StepFinishedMessageEventPart;

export interface MessageLifecyclePayload {
  info: MessageEventInfo;
  parts: MessageEventPart[];
}

export interface UserPromptCommittedPayload extends MessageLifecyclePayload {
  correlationId?: string | null;
}

export interface MessagePartUpdatedPayload {
  sessionID: string;
  part: MessageEventPart;
}

export interface MessagePartDeltaStreamedPayload {
  sessionID: string;
  messageID: string;
  partID: string;
  field: string;
  delta: string;
}

export interface DelegationCreatedPayload {
  delegationId: string;
  parentSessionId: string;
  parentToolCallId: string | null;
  childSessionId: string | null;
  title: string;
  status: string;
  createdAt: string;
}

export interface DelegationUpdatedPayload {
  delegationId: string;
  parentSessionId: string;
  parentToolCallId: string | null;
  childSessionId: string | null;
  title: string;
  status: string;
  createdAt: string;
}

export interface DelegationCompletedPayload {
  delegationId: string;
  parentSessionId: string;
  parentToolCallId: string | null;
  childSessionId: string | null;
  title: string;
  status: string;
  createdAt: string;
  completedAt: string;
}

export interface SessionActionCapabilities {
  canSend: boolean;
  canAbort: boolean;
  canArchive: boolean;
  canDelete: boolean;
}

export interface ActivityStatusPayload {
  sessionId: string;
  activityStatus: string;
  capabilities: SessionActionCapabilities;
  attempt?: number | null;
  message?: string | null;
  next?: string | null;
}

export interface FilesChangedPayload {
  sessionId: string;
  files: Array<{ path: string; changeType: string }>;
}

export interface CanvasUpdatedPayload {
  sessionId: string;
  canvasId: string;
  kind: string;
  title: string;
  version: number;
  actor: "agent" | "user";
  /** The whole canvas state, positions included. */
  state: JsonValue;
  summary: string;
}

/**
 * An app Fleet runs for a session, usually a dev server a browser canvas shows. `stopped` means Fleet stopped
 * it; `exited` that it ended on its own. `build-failed` comes with the live-update loop.
 */
export type AppRunStatus = "starting" | "running" | "build-failed" | "exited" | "stopped";

/** What just happened to an app: started, ready, restarted, stopped, exited, build-failed or reloaded. */
export type AppChangeReason = "started" | "ready" | "restarted" | "stopped" | "exited" | "build-failed" | "reloaded";

export interface AppUpdatedPayload {
  sessionId: string;
  appId: string;
  command: string;
  status: AppRunStatus;
  /** The page Fleet found, once one answered. */
  url: string | null;
  ports: number[];
  exitCode: number | null;
  reason: AppChangeReason;
}

/** A terminal tab in a session's drawer. `exitCode` is there only when the shell ended on its own. */
export interface TerminalPayload {
  sessionId: string;
  terminalId: string;
  title: string;
  exitCode?: number | null;
}

export interface CanvasRefPayload {
  sessionId: string;
  canvasId: string;
}

export interface SessionStarted extends EventCursorMetadata {
  type: "session.started";
  payload: SessionStartedPayload;
}

export interface SessionIdled extends EventCursorMetadata {
  type: "session.idled";
  payload: SessionIdledPayload;
}

export interface SessionDeleted extends EventCursorMetadata {
  type: "session.deleted";
  payload: SessionDeletedPayload;
}

export interface SessionArchived extends EventCursorMetadata {
  type: "session.archived";
  payload: SessionArchivedPayload;
}

export interface TurnStarted extends EventCursorMetadata {
  type: "turn.started";
  payload: TurnStartedPayload;
}

export interface TurnEnded extends EventCursorMetadata {
  type: "turn.ended";
  payload: TurnEndedPayload;
}

export interface TurnFailed extends EventCursorMetadata {
  type: "turn.failed";
  payload: TurnFailedPayload;
}

export interface MessageCreated extends EventCursorMetadata {
  type: "message.created";
  payload: MessageLifecyclePayload;
}

export interface MessageUpdated extends EventCursorMetadata {
  type: "message.updated";
  payload: MessageLifecyclePayload;
}

export interface UserPromptCommitted extends EventCursorMetadata {
  type: "user.prompt.committed";
  payload: UserPromptCommittedPayload;
}

export interface MessagePartUpdated extends EventCursorMetadata {
  type: "message.part.updated";
  payload: MessagePartUpdatedPayload;
}

export interface MessagePartDeltaStreamed extends EventCursorMetadata {
  type: "message.part.delta.streamed";
  payload: MessagePartDeltaStreamedPayload;
}

export interface DelegationCreated extends EventCursorMetadata {
  type: "delegation.created";
  payload: DelegationCreatedPayload;
}

export interface DelegationUpdated extends EventCursorMetadata {
  type: "delegation.updated";
  payload: DelegationUpdatedPayload;
}

export interface DelegationCompleted extends EventCursorMetadata {
  type: "delegation.completed";
  payload: DelegationCompletedPayload;
}

export interface ActivityStatus extends EventCursorMetadata {
  type: "activity_status";
  payload: ActivityStatusPayload;
}

export interface FilesChanged extends EventCursorMetadata {
  type: "files.changed";
  payload: FilesChangedPayload;
}

/** A canvas was opened, reopened or changed. Not persisted: it carries no event id. */
export interface CanvasUpdated extends EventCursorMetadata {
  type: "canvas.updated";
  payload: CanvasUpdatedPayload;
}

export interface CanvasClosed extends EventCursorMetadata {
  type: "canvas.closed";
  payload: CanvasRefPayload;
}

export interface CanvasFocused extends EventCursorMetadata {
  type: "canvas.focused";
  payload: CanvasRefPayload;
}

export type CanvasEvent = CanvasUpdated | CanvasClosed | CanvasFocused;

/**
 * A session's recap: one or two sentences Fleet wrote while you were away, on
 * the goal, the current task and the next action. `text` is empty when your
 * next prompt cleared it.
 */
export interface SessionRecapPayload {
  sessionId: string;
  text?: string | null;
  writtenAt?: string | null;
}

/** Fleet wrote or cleared the session's recap. Not persisted. */
export interface SessionRecap extends EventCursorMetadata {
  type: "session.recap";
  payload: SessionRecapPayload;
}

export function isSessionRecapEvent(event: DomainEvent): event is SessionRecap {
  return event.type === "session.recap";
}

/** An app Fleet runs for the session changed: the app as it is now, and why. Not persisted. */
export interface AppUpdated extends EventCursorMetadata {
  type: "app.updated";
  payload: AppUpdatedPayload;
}

export function isAppEvent(event: DomainEvent): event is AppUpdated {
  return event.type === "app.updated";
}

/** A terminal tab was added to the session's drawer. Not persisted. */
export interface TerminalOpened extends EventCursorMetadata {
  type: "terminal.opened";
  payload: TerminalPayload;
}

/** A terminal tab went away: closed by someone, or its shell ended. Not persisted. */
export interface TerminalClosed extends EventCursorMetadata {
  type: "terminal.closed";
  payload: TerminalPayload;
}

export type TerminalEvent = TerminalOpened | TerminalClosed;

export function isTerminalEvent(event: DomainEvent): event is TerminalEvent {
  return event.type === "terminal.opened" || event.type === "terminal.closed";
}

export function isCanvasEvent(event: DomainEvent): event is CanvasEvent {
  return event.type === "canvas.updated" || event.type === "canvas.closed" || event.type === "canvas.focused";
}

export type DomainEvent =
  | SessionStarted
  | SessionIdled
  | SessionDeleted
  | SessionArchived
  | TurnStarted
  | TurnEnded
  | TurnFailed
  | MessageCreated
  | MessageUpdated
  | UserPromptCommitted
  | MessagePartUpdated
  | MessagePartDeltaStreamed
  | DelegationCreated
  | DelegationUpdated
  | DelegationCompleted
  | ActivityStatus
  | FilesChanged
  | CanvasUpdated
  | CanvasClosed
  | CanvasFocused
  | TerminalOpened
  | TerminalClosed
  | AppUpdated
  | SessionRecap;
