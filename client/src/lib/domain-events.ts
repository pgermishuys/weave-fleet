import type { MemorySavedPayload } from "@/lib/agent-memory";
import type { SessionProgressDetail, SessionProgressSummary } from "@/lib/session-progress";
import type { SmartLinkWire } from "@/lib/smart-links";
import type { WorkflowRun } from "@/lib/workflows";

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
  /** The model provider's limit that stopped the turn; Fleet tries such a turn again by itself. */
  kind?: "rate_limit" | "usage_limit" | "overloaded" | null;
  /** When the provider said a request can go again (ISO), when it said. */
  retryAt?: string | null;
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

/** A slash command as the user sent it: `/name arguments`. */
export interface SlashCommand {
  name: string;
  arguments?: string | null;
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
  /** Set on a prompt the user sent into a running turn (steered). */
  steered?: boolean | null;
  /** The slash command a user message came from; its text is then what the harness made of the command. */
  command?: SlashCommand | null;
  /** Set on the summary a compaction wrote as a message of its own (OpenCode's); it shows behind the divider. */
  compactionSummary?: boolean | null;
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

/** Where the harness compacted the conversation: a divider, every harness the same way. */
export interface CompactionMessageEventPart extends BaseMessageEventPart {
  type: "compaction";
  /** "auto" or "manual", when known. */
  trigger?: string | null;
  tokensBefore?: number | null;
  tokensAfter?: number | null;
  /** The summary the model goes on from, when the harness gives it. */
  summary?: string | null;
}

export type MessageEventPart =
  | CompactionMessageEventPart
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
  /** Where the delta starts in the part's text; absent from a Fleet that doesn't count. */
  offset?: number | null;
}

export interface DelegationCreatedPayload {
  delegationId: string;
  parentSessionId: string;
  parentToolCallId: string | null;
  childSessionId: string | null;
  title: string;
  status: string;
  createdAt: string;
  /** Set when the sub-agent works on in the background, its call having returned. */
  background?: boolean;
}

export interface DelegationUpdatedPayload {
  delegationId: string;
  parentSessionId: string;
  parentToolCallId: string | null;
  childSessionId: string | null;
  title: string;
  status: string;
  createdAt: string;
  /** Set when the sub-agent works on in the background, its call having returned. */
  background?: boolean;
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
  /** Set when the sub-agent works on in the background, its call having returned. */
  background?: boolean;
}

/** A running-work item as the server sends it: nulls and false left out. Read it with `toRunningWorkItem`. */
export type RunningWorkItemPayload = Record<string, unknown> & { id: string; sessionId: string };

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

/**
 * Work a session's agent left running started, changed or ended. The payload is the whole item as it is now (see
 * `@/lib/running-work`); it arrives on the session's topic and on `sessions`, so every client can count it.
 */
export interface WorkStarted extends EventCursorMetadata {
  type: "work.started";
  payload: RunningWorkItemPayload;
}

export interface WorkUpdated extends EventCursorMetadata {
  type: "work.updated";
  payload: RunningWorkItemPayload;
}

export interface WorkEnded extends EventCursorMetadata {
  type: "work.ended";
  payload: RunningWorkItemPayload;
}

export type WorkEvent = WorkStarted | WorkUpdated | WorkEnded;

export function isWorkEvent(event: DomainEvent): event is WorkEvent {
  return event.type === "work.started" || event.type === "work.updated" || event.type === "work.ended";
}

/**
 * How full the session's context window is changed: a model call, the model's window, or a compaction. The payload
 * is the whole record as it is now (see `@/lib/context-usage`), on the session's topic.
 */
export interface ContextUpdated extends EventCursorMetadata {
  type: "context.updated";
  payload: unknown;
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

/**
 * One thing worth interrupting you for: a session that needed you, or finished, while you were looking
 * somewhere else. Sent on the global sessions topic; the browser turns it into a desktop notification.
 */
export interface SessionNotificationPayload {
  sessionId: string;
  reason: "needs_you" | "finished" | "failed";
  /** What exactly it's about. Older Fleets send none. */
  kind?: "permission" | "question" | "finished" | "failed" | "workflow";
  /** The permission the agent waits on, for a `permission`. */
  requestId?: string | null;
  /** The machine the session is on. */
  machineId?: string | null;
  machineName?: string | null;
  title: string;
  body: string;
}

/** A session you weren't looking at needs you, or finished. Not persisted. */
export interface SessionNotification extends EventCursorMetadata {
  type: "session_notification";
  payload: SessionNotificationPayload;
}

export function isSessionNotificationEvent(event: DomainEvent): event is SessionNotification {
  return event.type === "session_notification";
}

/** An app Fleet runs for the session changed: the app as it is now, and why. Not persisted. */
export interface AppUpdated extends EventCursorMetadata {
  type: "app.updated";
  payload: AppUpdatedPayload;
}

export function isAppEvent(event: DomainEvent): event is AppUpdated {
  return event.type === "app.updated";
}

/** One action the agent took in its own browser tab (`BrowserStep` on the server). */
export interface BrowserStep {
  sessionId: string;
  /** Its place in the session's steps, from 1. */
  seq: number;
  at: string;
  /** The operation, e.g. `click` or `tabs.open`. */
  kind: string;
  /** What happened in plain words: `Clicked “Save”`. */
  summary: string;
  /** The operation and how long it took: `click @e2 · 170 ms`. */
  detail: string;
  ok: boolean;
  error?: string | null;
  /** The tool call that took it, when Fleet saw one running. */
  callId?: string | null;
  tabId?: string | null;
  url?: string | null;
  title?: string | null;
  /** Where the element acted on was on the tab's 1280×800 screen. */
  box?: { x: number; y: number; width: number; height: number } | null;
  /** A screenshot the step took, kept under the session. */
  screenshot?: { id: string; width: number; height: number } | null;
}

/** The agent took a step in its own browser tab. Kept by Fleet; loaded with `GET /api/sessions/{id}/agent-browser`. */
export interface BrowserStepped extends EventCursorMetadata {
  type: "browser.step";
  payload: BrowserStep;
}

export function isBrowserStepEvent(event: DomainEvent): event is BrowserStepped {
  return event.type === "browser.step";
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

/**
 * A session's queue changed: the messages its user queued while the agent worked, in the order Fleet sends them.
 * Carries the whole queue. Not persisted; the queue itself is loaded from `GET /api/sessions/{id}/queue`.
 */
export interface SessionQueueChanged extends EventCursorMetadata {
  type: "session.queue";
  payload: {
    sessionId: string;
    items: { id: string; kind: string; text: string; createdAt: string }[];
  };
}

/**
 * When Fleet tries a turn a model provider's limit stopped again changed: scheduled, sent, or called off. Sent on the
 * session's topic and on `sessions`. `retry` is null when none waits. Not persisted; it loads from
 * `GET /api/sessions/{id}/retry`.
 */
export interface SessionRetryChanged extends EventCursorMetadata {
  type: "session.retry";
  payload: {
    sessionId: string;
    retry: { dueAt: string; attempt: number; kind: string; reason: string; providerSaid: boolean } | null;
  };
}

/**
 * The agent asks to do something the session's permission level doesn't allow. Sent on the topic of the session it's
 * shown on (a subagent's on the session it works for). Not persisted; waiting asks load from
 * `GET /api/sessions/{id}/permissions`.
 */
export interface PermissionAsked extends EventCursorMetadata {
  type: "permission.asked";
  payload: Record<string, unknown>;
}

/** An ask was answered, or went away with its turn: `reply` is once, always, reject or gone. */
export interface PermissionReplied extends EventCursorMetadata {
  type: "permission.replied";
  payload: { id: string; sessionId: string; reply: string };
}

export function isCanvasEvent(event: DomainEvent): event is CanvasEvent {
  return event.type === "canvas.updated" || event.type === "canvas.closed" || event.type === "canvas.focused";
}

// Events the server sends that no reducer reads. Subscribers read them with `onDomainEvent`
// (composables/on-domain-event.ts), which hands the handler the payload typed. Payloads are camelCase, as the server
// writes them.

/** One session messaged another (`fleet_message`). Sent on the receiver's topic. */
export interface SessionMessaged extends EventCursorMetadata {
  type: "session.messaged";
  payload: {
    fromSessionId: string;
    /** The machine the sender is on, when it's another machine. */
    fromMachineId?: string | null;
    toSessionId: string;
    eventId?: number | null;
    correlationId: string;
  };
}

/** A session that was messaged with `notifyWhenDone` finished the turn and Fleet told the asker. Sent on the asker's topic. */
export interface SessionReported extends EventCursorMetadata {
  type: "session.reported";
  payload: {
    fromSessionId: string;
    toSessionId: string;
    /** How the turn ended: `finished` or `failed`. */
    outcome: string;
    correlationId: string;
  };
}

/** The agent's todo list changed. Carries the whole list; an empty one means the agent cleared it. */
export interface TodosReported extends EventCursorMetadata {
  type: "todos.reported";
  payload: {
    sessionId: string;
    items: { content: string; status: string; priority?: string | null }[];
  };
}

/** The agent finished writing files with a tool: an edit, a new file, a patch. */
export interface FilesWritten extends EventCursorMetadata {
  type: "files.written";
  payload: {
    sessionId: string;
    messageId?: string | null;
    /** Absolute paths. */
    paths: string[];
  };
}

/** A harness's own `session.status` event, as it arrives when no domain event stands in for it. Nothing in the client reads it. */
export interface SessionStatusReported extends EventCursorMetadata {
  type: "session.status";
  payload: Record<string, unknown>;
}

/** A session was created or forked. Sent on the `sessions` topic. */
export interface SessionListCreated extends EventCursorMetadata {
  type: "session_created";
  payload: {
    sessionId: string;
    instanceId?: string | null;
    workspaceId?: string | null;
    title?: string | null;
    projectId?: string | null;
    parentSessionId?: string | null;
    isHidden?: boolean | null;
    forkedFromSessionId?: string | null;
    spawnedBySessionId?: string | null;
    spawnKind?: string | null;
  };
}

/** A session was archived. Sent on the `sessions` topic. */
export interface SessionListArchived extends EventCursorMetadata {
  type: "session_archived";
  payload: { sessionId: string; archivedAt: string };
}

/** A session was restored from the archive. Sent on the `sessions` topic. */
export interface SessionListUnarchived extends EventCursorMetadata {
  type: "session_unarchived";
  payload: { sessionId: string };
}

/** A session was deleted. Sent on the `sessions` topic. */
export interface SessionListDeleted extends EventCursorMetadata {
  type: "session_deleted";
  payload: { sessionId: string };
}

/** A session's progress summary changed, for its row. Sent on the `sessions` topic. */
export interface SessionProgressChanged extends EventCursorMetadata {
  type: "session_progress";
  payload: SessionProgressSummary;
}

/** A session's progress, with its todos and plan. Sent on the session's topic. */
export interface ProgressUpdated extends EventCursorMetadata {
  type: "progress.updated";
  payload: SessionProgressDetail;
}

/** A session's token and cost totals after a turn was counted. Sent on the `sessions` topic. */
export interface SessionTokensCounted extends EventCursorMetadata {
  type: "session_tokens";
  payload: { sessionId: string; totalTokens: number; totalCost: number };
}

/** A pull request or other link of a session changed. Sent on the `sessions` topic. */
export interface SmartLinkUpdated extends EventCursorMetadata {
  type: "smart_link.updated";
  payload: SmartLinkWire;
}

/** An agent saved a memory note. Sent on the `sessions` topic. */
export interface MemorySaved extends EventCursorMetadata {
  type: "memory.saved";
  payload: MemorySavedPayload;
}

/** A workflow run changed. Sent on the `sessions` topic. */
export interface WorkflowRunChanged extends EventCursorMetadata {
  type: "workflow_run";
  payload: WorkflowRun;
}

/** A harness's usage limits changed. Sent on the `sessions` topic. */
export interface HarnessUsageChanged extends EventCursorMetadata {
  type: "harness.usage";
  payload: {
    harnessType: string;
    windows: { window: string; utilization?: number | null; resetsAt?: string | null; status?: string }[];
  };
}

/** What a harness offers in a folder (agents, models, commands) changed while it ran. Sent on the `sessions` topic. */
export interface HarnessCatalogChanged extends EventCursorMetadata {
  type: "harness.catalog_changed";
  payload: {
    harnessType: string;
    directory: string;
    quickChat?: boolean;
    profileIds?: string[];
    sessionIds?: string[];
  };
}

/** A mod was kept, undone, turned on or off, failed three times, or safe mode changed. Sent on the `sessions` topic. */
export interface ModsChanged extends EventCursorMetadata {
  type: "mods.changed";
  payload: {
    name?: string | null;
    reason: "kept" | "version" | "undone" | "on" | "off" | "strikes" | "draft-off" | "draft-on" | "safe-mode";
    sessionId?: string | null;
  };
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
  | WorkStarted
  | WorkUpdated
  | WorkEnded
  | ContextUpdated
  | ActivityStatus
  | FilesChanged
  | CanvasUpdated
  | CanvasClosed
  | CanvasFocused
  | TerminalOpened
  | TerminalClosed
  | AppUpdated
  | BrowserStepped
  | SessionRecap
  | SessionNotification
  | SessionQueueChanged
  | SessionRetryChanged
  | PermissionAsked
  | PermissionReplied
  | SessionMessaged
  | SessionReported
  | TodosReported
  | FilesWritten
  | SessionStatusReported
  | SessionListCreated
  | SessionListArchived
  | SessionListUnarchived
  | SessionListDeleted
  | SessionProgressChanged
  | ProgressUpdated
  | SessionTokensCounted
  | SmartLinkUpdated
  | MemorySaved
  | WorkflowRunChanged
  | HarnessUsageChanged
  | HarnessCatalogChanged
  | ModsChanged;
