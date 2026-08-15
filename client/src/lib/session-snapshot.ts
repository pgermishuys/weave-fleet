import type { DelegationDto } from "@/lib/client-types";
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
  lastEventId: number | null;
  /** Deprecated compatibility alias for lastEventId during the migration. */
  lastSequenceNumber?: number | null;
  hasMore: boolean;
  cursor: string | null;
  /** Indicates whether this snapshot is partial due to unavailability of the live harness. */
  isPartial: boolean;
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
