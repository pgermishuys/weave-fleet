/**
 * Bare-bones smoke test for the beta-harness loop. Not a regression test — it verifies that
 * fleet boots in test mode, accepts a session pinned to a known scenario, the harness emits
 * the scripted events, and the messages API returns the assistant text we asked for.
 *
 * Usage:
 *   bun run tsx tests/beta-harness/smoke-test.ts
 */

import { setTimeout as sleep } from "node:timers/promises";
import { startFleet } from "./start-fleet.js";
import { newSession, sendPrompt, tailLog, recordFinding } from "./helpers/index.js";

const SCENARIO_ID = "model-persistence-refresh";

interface FleetMessage {
  id: string;
  role: "user" | "assistant" | string;
  parts: Array<{ type: string; text?: string }>;
}

async function pollMessages(
  baseUrl: string,
  sessionId: string,
  predicate: (messages: FleetMessage[]) => boolean,
  timeoutMs: number,
): Promise<FleetMessage[]> {
  const deadline = Date.now() + timeoutMs;
  let last: FleetMessage[] = [];
  while (Date.now() < deadline) {
    const resp = await fetch(`${baseUrl}/api/sessions/${encodeURIComponent(sessionId)}/messages`);
    if (resp.ok) {
      const data = (await resp.json()) as { messages?: FleetMessage[] };
      last = data.messages ?? [];
      if (predicate(last)) return last;
    }
    await sleep(250);
  }
  return last;
}

async function main(): Promise<void> {
  process.stdout.write(`smoke: starting fleet (--harness=test, scenario=${SCENARIO_ID})\n`);
  const fleet = await startFleet();
  try {
    process.stdout.write(`smoke: fleet healthy at ${fleet.baseUrl}\n`);

    const session = await newSession({
      baseUrl: fleet.baseUrl,
      scenarioId: SCENARIO_ID,
    });
    process.stdout.write(`smoke: session ${session.sessionId} created on instance ${session.instanceId}\n`);

    await sendPrompt({
      baseUrl: fleet.baseUrl,
      sessionId: session.sessionId,
      text: "Hello, world!",
    });
    process.stdout.write("smoke: prompt sent, polling /messages for assistant reply\n");

    // Match the scripted assistant text (contains the [beta-harness] marker) rather than any
    // assistant message — the persister's role-inference path can mark message.part.updated
    // rows with role=assistant by default, which would mask whether the scenario actually
    // ran end-to-end.
    const messages = await pollMessages(
      fleet.baseUrl,
      session.sessionId,
      (msgs) => msgs.some((m) =>
        (m.parts ?? []).some((p) => p.type === "text" && (p.text ?? "").includes("[beta-harness]"))
      ),
      15_000,
    );

    const assistant = messages.find((m) =>
      (m.parts ?? []).some((p) => p.type === "text" && (p.text ?? "").includes("[beta-harness]"))
    );
    const assistantText = (assistant?.parts ?? [])
      .filter((p) => p.type === "text")
      .map((p) => p.text ?? "")
      .join("");

    if (!assistant) {
      const tail = tailLog({ grep: "(?i)error|harness|scenario", lines: 50 });
      const path = recordFinding({
        scenarioId: SCENARIO_ID,
        result: "suspected-bug",
        repro: [
          "Start fleet with --harness=test.",
          `POST /api/sessions with scenarioId=${SCENARIO_ID}.`,
          `POST /api/sessions/{id}/prompt with text="Hello, world!".`,
          "Poll GET /api/sessions/{id}/messages for an assistant message.",
        ],
        evidence: [
          `Polled for 15s; no assistant message arrived.`,
          `Last messages: ${JSON.stringify(messages)}`,
          `Log tail: ${tail.length} matching lines`,
          ...tail.slice(-5),
        ],
        nextProbe: "Inspect fleet.log for harness-side errors. Verify ScenarioId is reaching HarnessSpawnOptions.",
      });
      process.stderr.write(`smoke: FAIL — no assistant message. Finding: ${path}\n`);
      process.exit(1);
    }

    process.stdout.write(`smoke: assistant text: ${assistantText.slice(0, 120)}…\n`);

    const path = recordFinding({
      scenarioId: SCENARIO_ID,
      result: "pass",
      repro: [
        "Start fleet with --harness=test.",
        `POST /api/sessions with scenarioId=${SCENARIO_ID}.`,
        `POST /api/sessions/{id}/prompt with text="Hello, world!".`,
        "Poll GET /api/sessions/{id}/messages for an assistant message.",
      ],
      evidence: [
        `Assistant message id: ${assistant.id}`,
        `Assistant text: ${assistantText.slice(0, 200)}`,
      ],
      nextProbe: "Drive the same flow through the SPA to verify model selection round-trips after refresh.",
    });
    process.stdout.write(`smoke: PASS — finding written to ${path}\n`);
  } finally {
    await fleet.stop();
    process.stdout.write("smoke: fleet stopped\n");
  }
}

await main();
