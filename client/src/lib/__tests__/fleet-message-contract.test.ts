import { describe, it, expect } from "vitest";
import { readFileSync } from "fs";
import { resolve } from "path";
import { convertFleetMessageToAccumulated } from "@/lib/pagination-utils";
import type { FleetMessage } from "@/lib/pagination-utils";

// Load shared contract fixtures from tests/contracts/ (relative to repo root)
const fixturesDir = resolve(__dirname, "../../../../tests/contracts");

function loadFixture(filename: string) {
  const raw = readFileSync(resolve(fixturesDir, filename), "utf-8");
  return JSON.parse(raw);
}

describe("Fleet API → AccumulatedMessage contract", () => {
  const fixture = loadFixture("fleet-api-messages.json");
  const messages: FleetMessage[] = fixture.messages_response.messages;
  const expectedAccumulated = fixture.expected_accumulated;

  it("has matching message count", () => {
    expect(messages.length).toBe(expectedAccumulated.length);
  });

  messages.forEach((msg: FleetMessage, i: number) => {
    it(`converts message "${msg.id}" to expected AccumulatedMessage shape`, () => {
      const actual = convertFleetMessageToAccumulated(msg);
      const expected = expectedAccumulated[i];

      expect(actual.messageId).toBe(expected.messageId);
      expect(actual.role).toBe(expected.role);
      expect(actual.sessionId).toBe(expected.sessionId);
      expect(actual.parts.length).toBe(expected.parts.length);

      for (let p = 0; p < expected.parts.length; p++) {
        const actualPart = actual.parts[p];
        const expectedPart = expected.parts[p];
        expect(actualPart.partId).toBe(expectedPart.partId);
        expect(actualPart.type).toBe(expectedPart.type);

        if (expectedPart.type === "text") {
          expect((actualPart as { text: string }).text).toBe(expectedPart.text);
        }
        if (expectedPart.type === "reasoning") {
          expect((actualPart as { text: string }).text).toBe(expectedPart.text);
        }
        if (expectedPart.type === "tool") {
          expect((actualPart as { tool: string }).tool).toBe(expectedPart.tool);
          expect((actualPart as { callId: string }).callId).toBe(
            expectedPart.callId,
          );
          expect((actualPart as { state: unknown }).state).toEqual(
            expectedPart.state,
          );
        }
      }
    });
  });
});
