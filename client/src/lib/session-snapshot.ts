import type { DelegationDto } from "@/lib/api-types";
import type { MessageLifecyclePayload } from "@/lib/domain-events";

export interface SessionSnapshotSession {
  id: string;
  title: string;
  status: string;
}

export interface SessionSnapshotDelegation {
  delegationId: string;
  parentToolCallId: string | null;
  childSessionId: string | null;
  title: string;
  status: DelegationDto["status"];
  createdAt: string;
}

export interface SessionSnapshot {
  session: SessionSnapshotSession;
  messages: MessageLifecyclePayload[];
  delegations: SessionSnapshotDelegation[];
  activityStatus: string;
  lastSequenceNumber: number | null;
  hasMore: boolean;
  cursor: string | null;
}

export interface SessionHistoryPage {
  messages: MessageLifecyclePayload[];
  cursor: string | null;
  hasMore: boolean;
}

export interface HistoryResponse {
  type: "history";
  topic: string;
  data: SessionHistoryPage;
}
