import { describe, expect, it } from "vitest";
import { applyDelegationCreated, applyDelegationUpdated } from "@/lib/delegation-state";
import type { DelegationDto } from "@/lib/api-types";

describe("delegation-state", () => {
  it("applyDelegationCreated appends a new delegation", () => {
    const result = applyDelegationCreated([], {
      delegationId: "del-1",
      parentToolCallId: "tool-1",
      childSessionId: null,
      title: "reviewer",
      status: "pending",
    });

    expect(result).toEqual<DelegationDto[]>([
      {
        delegationId: "del-1",
        parentToolCallId: "tool-1",
        childSessionId: null,
        title: "reviewer",
        status: "pending",
        createdAt: null,
      },
    ]);
  });

  it("applyDelegationCreated is idempotent for existing delegation", () => {
    const prev: DelegationDto[] = [
      {
        delegationId: "del-1",
        parentToolCallId: "tool-1",
        childSessionId: null,
        title: "reviewer",
        status: "pending",
        createdAt: null,
      },
    ];

    const result = applyDelegationCreated(prev, {
      delegationId: "del-1",
      parentToolCallId: "tool-1",
      childSessionId: null,
      title: "reviewer",
      status: "pending",
    });

    expect(result).toEqual(prev);
  });

  it("applyDelegationUpdated merges updates by delegationId", () => {
    const prev: DelegationDto[] = [
      {
        delegationId: "del-1",
        parentToolCallId: "tool-1",
        childSessionId: null,
        title: "reviewer",
        status: "pending",
      },
    ];

    const result = applyDelegationUpdated(prev, {
      delegationId: "del-1",
      childSessionId: "child-1",
      status: "running",
    });

    expect(result).toEqual<DelegationDto[]>([
      {
        delegationId: "del-1",
        parentToolCallId: "tool-1",
        childSessionId: "child-1",
        title: "reviewer",
        status: "running",
        createdAt: null,
      },
    ]);
  });

  it("applyDelegationUpdated preserves createdAt when omitted from update", () => {
    const prev: DelegationDto[] = [
      {
        delegationId: "del-1",
        parentToolCallId: "tool-1",
        childSessionId: null,
        title: "reviewer",
        status: "pending",
        createdAt: "2026-04-10T12:00:00.000Z",
      },
    ];

    const result = applyDelegationUpdated(prev, {
      delegationId: "del-1",
      status: "running",
    });

    expect(result[0]?.createdAt).toBe("2026-04-10T12:00:00.000Z");
  });

  it("applyDelegationUpdated ignores unknown delegations", () => {
    const prev: DelegationDto[] = [];

    const result = applyDelegationUpdated(prev, {
      delegationId: "missing",
      status: "completed",
    });

    expect(result).toBe(prev);
  });
});
