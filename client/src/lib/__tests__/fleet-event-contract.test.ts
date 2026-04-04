import { describe, it, expect, vi } from "vitest";
import { readFileSync } from "fs";
import { resolve } from "path";
import type React from "react";
import type { AccumulatedMessage, SSEEvent } from "@/lib/api-types";
import { handleEvent } from "@/hooks/use-session-events";

const fixturesDir = resolve(__dirname, "../../../../tests/contracts");

function loadFixture(filename: string) {
  const raw = readFileSync(resolve(fixturesDir, filename), "utf-8");
  return JSON.parse(raw);
}

function createStateHarness(sessionId: string) {
  let messages: AccumulatedMessage[] = [];
  let status: string | undefined;
  let sessionStatus: "idle" | "busy" = "idle";
  let error: string | undefined;

  const setMessages = (update: React.SetStateAction<AccumulatedMessage[]>) => {
    messages =
      typeof update === "function"
        ? (update as (prev: AccumulatedMessage[]) => AccumulatedMessage[])(
            messages,
          )
        : update;
  };
  const setStatus = (update: React.SetStateAction<string>) => {
    status =
      typeof update === "function"
        ? (update as (prev: string | undefined) => string)(status)
        : update;
  };
  const setSessionStatus = (
    update: React.SetStateAction<"idle" | "busy">,
  ) => {
    sessionStatus =
      typeof update === "function"
        ? (update as (prev: "idle" | "busy") => "idle" | "busy")(sessionStatus)
        : update;
  };
  const setError = (update: React.SetStateAction<string | undefined>) => {
    error =
      typeof update === "function"
        ? (update as (prev: string | undefined) => string | undefined)(error)
        : update;
  };

  const onAgentSwitchRef: React.MutableRefObject<
    ((agent: string) => void) | undefined
  > = { current: vi.fn() };
  const lastMessageIdRef: React.MutableRefObject<string | null> = {
    current: null,
  };

  const dispatch = (event: SSEEvent) => {
    handleEvent(
      event,
      sessionId,
      setMessages,
      setStatus as React.Dispatch<
        React.SetStateAction<
          | "connecting"
          | "connected"
          | "recovering"
          | "disconnected"
          | "error"
          | "abandoned"
        >
      >,
      setSessionStatus,
      setError,
      onAgentSwitchRef,
      lastMessageIdRef,
    );
  };

  return { dispatch, getMessages: () => messages };
}

describe("Fleet WebSocket events → AccumulatedMessage state contract", () => {
  const fixture = loadFixture("fleet-api-events.json");

  for (const testCase of fixture.cases) {
    it(`handles "${testCase.name as string}"`, () => {
      const harness = createStateHarness(testCase.session_id as string);

      // Replay all events in sequence
      for (const event of testCase.events) {
        harness.dispatch(event as SSEEvent);
      }

      const messages = harness.getMessages();
      expect(messages.length).toBe(
        (testCase.expected_messages as unknown[]).length,
      );

      for (
        let m = 0;
        m < (testCase.expected_messages as unknown[]).length;
        m++
      ) {
        const actual = messages[m];
        const expected = testCase.expected_messages[m] as {
          messageId: string;
          sessionId: string;
          role: string;
          parts: Array<{
            partId: string;
            type: string;
            text?: string;
            tool?: string;
            callId?: string;
            state?: unknown;
          }>;
        };

        expect(actual.messageId).toBe(expected.messageId);
        expect(actual.sessionId).toBe(expected.sessionId);
        expect(actual.role).toBe(expected.role);
        expect(actual.parts.length).toBe(expected.parts.length);

        for (let p = 0; p < expected.parts.length; p++) {
          expect(actual.parts[p].partId).toBe(expected.parts[p].partId);
          expect(actual.parts[p].type).toBe(expected.parts[p].type);

          if (expected.parts[p].type === "text") {
            expect((actual.parts[p] as { text: string }).text).toBe(
              expected.parts[p].text,
            );
          }
          if (expected.parts[p].type === "tool") {
            expect((actual.parts[p] as { tool: string }).tool).toBe(
              expected.parts[p].tool,
            );
            expect((actual.parts[p] as { callId: string }).callId).toBe(
              expected.parts[p].callId,
            );
            expect((actual.parts[p] as { state: unknown }).state).toEqual(
              expected.parts[p].state,
            );
          }
        }
      }
    });
  }
});
