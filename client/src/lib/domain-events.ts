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

export interface SessionStoppedPayload {
  sessionId: string;
  stoppedAt: string;
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
}

export interface ToolErrorState {
  status: "error";
  input: JsonValue | null;
  output: JsonValue | null;
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

export interface SessionStarted extends EventCursorMetadata {
  type: "session.started";
  payload: SessionStartedPayload;
}

export interface SessionIdled extends EventCursorMetadata {
  type: "session.idled";
  payload: SessionIdledPayload;
}

export interface SessionStopped extends EventCursorMetadata {
  type: "session.stopped";
  payload: SessionStoppedPayload;
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

export type DomainEvent =
  | SessionStarted
  | SessionIdled
  | SessionStopped
  | SessionDeleted
  | SessionArchived
  | TurnStarted
  | TurnEnded
  | MessageCreated
  | MessageUpdated
  | UserPromptCommitted
  | MessagePartUpdated
  | MessagePartDeltaStreamed
  | DelegationCreated
  | DelegationUpdated
  | DelegationCompleted;
