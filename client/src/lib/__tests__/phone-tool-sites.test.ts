/**
 * The phone's tool sites on names the big table (tool-classification.test.ts) does not carry: short aliases, a case
 * the question checks used to miss, and the headings of a read ask for tools that are not file reads. Written first
 * against the old code; the expectations that changed with the move onto lib/tools are marked.
 */
import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import PermissionCard from "@/components/session/PermissionCard.vue";
import type { PermissionAsk } from "@/composables/use-session-permissions";
import type { AccumulatedMessage, AccumulatedToolPart } from "@/lib/client-types";
import { pendingQuestion } from "@/lib/phone/dock-state";
import { foldMessages, stepCategory, stepRow, type FoldedStep } from "@/lib/phone/fold-steps";
import { getQuestionInput, isQuestionPart } from "@/lib/question-types";
import { permissionHeading } from "@/lib/tools";

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
  it("files fetch under read and search under search; todowrite and execute stay other", () => {
    expect(["fetch", "search", "ls", "LS", "WebFetch"].map(stepCategory)).toEqual(["read", "search", "read", "read", "read"]);
    expect(["todowrite", "execute", "Agent"].map(stepCategory)).toEqual(["other", "other", "other"]);
    // Changed: these were "other".
    expect(["lsp", "terminal", "notebookedit", "strreplaceeditor"].map(stepCategory)).toEqual(["read", "run", "edit", "edit"]);
  });

  it("reads the file from filePath or path only, and the command only for run steps", () => {
    expect(stepRow(step("read", { path: "src/a.ts" })).detail).toBe("src/a.ts");
    // Changed: Claude Code's file_path was ignored, so the row said "read".
    expect(stepRow(step("read", { file_path: "src/a.ts" })).detail).toBe("src/a.ts");
    expect(stepRow(step("bash", { command: "ls -la" })).detail).toBe("ls -la");
  });

  it("makes an Agent call a subagent row (it was a plain step)", () => {
    expect(foldMessages([assistant([toolPart("Agent", { description: "look" })])]).map((b) => b.kind)).toEqual(["subagent"]);
  });
});

describe("the question checks", () => {
  it("match `question` in any case (Question used to be no question)", () => {
    const input = { questions: [{ question: "Which?", header: "h", options: [] }] };
    expect(isQuestionPart(toolPart("question", input))).toBe(true);
    expect(isQuestionPart(toolPart("Question", input))).toBe(true);
    expect(getQuestionInput(toolPart("Question", input))).not.toBeNull();
    expect(pendingQuestion([assistant([toolPart("Question", input, "running")])])).not.toBeNull();
    expect(pendingQuestion([assistant([toolPart("question", input, "running")])])).not.toBeNull();
  });
});

describe("a read ask for tools that are not file reads", () => {
  const ask = (tool: string): PermissionAsk => ({ id: "per_1", sessionId: "s1", kind: "read", tool, title: "x", always: ["*"], askedAt: "2026-10-01T10:00:00Z" });

  it("is 'Read a file' for a file read and 'Use <tool>' for the others, on the phone and the desktop card alike", () => {
    // Changed: the phone said "Read a file" for all of them, the card "Use <tool>" for all of them.
    for (const [tool, heading] of [["read", "Read a file"], ["grep", "Read a file"], ["todowrite", "Use todowrite"], ["task", "Use task"], ["skill", "Use skill"], ["fleet_page_show", "Use fleet_page_show"]]) {
      expect(permissionHeading(ask(tool))).toBe(heading);
      const wrapper = mount(PermissionCard, { props: { ask: ask(tool), onAnswer: async () => {} } });
      expect(wrapper.get(".pcard__title").text()).toBe(heading);
      wrapper.unmount();
    }
  });
});
