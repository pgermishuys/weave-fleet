/**
 * Characterization: the phone's tool sites on names the big table (tool-classification.test.ts) does not carry:
 * short aliases, a case the question checks miss, and the headings of a read ask for tools that are not file reads.
 * Pins today's answers so the move onto lib/tools shows exactly what changed.
 */
import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import PermissionCard from "@/components/session/PermissionCard.vue";
import type { PermissionAsk } from "@/composables/use-session-permissions";
import type { AccumulatedMessage, AccumulatedToolPart } from "@/lib/client-types";
import { pendingQuestion } from "@/lib/phone/dock-state";
import { permissionTitle } from "@/lib/phone/asks";
import { foldMessages, stepCategory, stepRow, type FoldedStep } from "@/lib/phone/fold-steps";
import { getQuestionInput, isQuestionPart } from "@/lib/question-types";

vi.mock("@tanstack/vue-router", () => ({ useNavigate: () => vi.fn() }));

function toolPart(tool: string, input: unknown, status = "completed"): AccumulatedToolPart {
  return { type: "tool", partId: `p-${tool}`, callId: `c-${tool}`, tool, state: { status, input } } as unknown as AccumulatedToolPart;
}

function assistant(parts: AccumulatedToolPart[]): AccumulatedMessage {
  return { messageId: "m1", role: "assistant", parts } as unknown as AccumulatedMessage;
}

function step(tool: string, input: unknown): FoldedStep {
  const block = foldMessages([assistant([toolPart(tool, input)])]).find((b) => b.kind === "steps");
  if (block?.kind !== "steps") throw new Error("no steps");
  return block.steps[0];
}

describe("the phone's step categories on short and odd names", () => {
  it("files fetch under read and search under search; Agent, lsp and Todo are other", () => {
    expect(["fetch", "search", "ls", "LS", "WebFetch"].map(stepCategory)).toEqual(["read", "search", "read", "read", "read"]);
    expect(["Agent", "lsp", "todowrite", "execute", "terminal", "notebookedit", "strreplaceeditor"].map(stepCategory)).toEqual([
      "other", "other", "other", "other", "other", "other", "other",
    ]);
  });

  it("reads the file from filePath or path only, and the command only for run steps", () => {
    expect(stepRow(step("read", { path: "src/a.ts" })).detail).toBe("src/a.ts");
    expect(stepRow(step("read", { file_path: "src/a.ts" })).detail).toBe("read");
    expect(stepRow(step("bash", { command: "ls -la" })).detail).toBe("ls -la");
  });

  it("makes an Agent call a plain step, not a subagent row", () => {
    expect(foldMessages([assistant([toolPart("Agent", { description: "look" })])]).map((b) => b.kind)).toEqual(["steps"]);
  });
});

describe("the question checks", () => {
  it("match `question` exactly: Question is not a question", () => {
    const input = { questions: [{ question: "Which?", header: "h", options: [] }] };
    expect(isQuestionPart(toolPart("question", input))).toBe(true);
    expect(isQuestionPart(toolPart("Question", input))).toBe(false);
    expect(getQuestionInput(toolPart("Question", input))).toBeNull();
    expect(pendingQuestion([assistant([toolPart("Question", input, "running")])])).toBeNull();
    expect(pendingQuestion([assistant([toolPart("question", input, "running")])])).not.toBeNull();
  });
});

describe("a read ask for tools that are not file reads", () => {
  const ask = (tool: string): PermissionAsk => ({ id: "per_1", sessionId: "s1", kind: "read", tool, title: "x", always: ["*"], askedAt: "2026-10-01T10:00:00Z" });

  it("is 'Read a file' on the phone and 'Use <tool>' on the desktop card, whatever the tool", () => {
    for (const tool of ["read", "todowrite", "task", "skill", "fleet_page_show"]) {
      expect(permissionTitle(ask(tool))).toBe("Read a file");
      const wrapper = mount(PermissionCard, { props: { ask: ask(tool), onAnswer: async () => {} } });
      expect(wrapper.get(".pcard__title").text()).toBe(`Use ${tool}`);
      wrapper.unmount();
    }
  });
});
