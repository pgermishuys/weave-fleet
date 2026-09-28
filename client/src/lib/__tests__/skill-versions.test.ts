import { describe, expect, it } from "vitest";
import { formatTokens, improveTurn, versionLabel, type ImproveMessage } from "@/lib/skill-versions";

const conversation: ImproveMessage[] = [
  { id: "u1", role: "user", body: "fix the tests" },
  { id: "a1", role: "assistant", body: "Done." },
  { id: "u2", role: "user", body: "review this branch" },
  {
    id: "a2",
    role: "assistant",
    body: "",
    tools: [
      { title: "fleet-code-review", kind: "skill", status: "Completed", output: "<skill_content>…</skill_content>" },
      { title: "git diff", kind: "bash", status: "Completed", output: "x".repeat(2000) },
    ],
  },
  { id: "a3", role: "assistant", body: "Three findings: 1. usr should be user." },
  { id: "u3", role: "user", body: "thanks" },
];

describe("improveTurn", () => {
  it("takes the prompt that started the turn and every reply up to the next prompt", () => {
    const turn = improveTurn(conversation, "a2");

    expect(turn.exchange).toBe(
      "The user asked:\nreview this branch\n\nThe agent answered:\nThree findings: 1. usr should be user.",
    );
    expect(turn.exchange).not.toContain("fix the tests");
    expect(turn.exchange).not.toContain("thanks");
  });

  it("lists the turn's tool calls with the start of each output", () => {
    const { toolCalls } = improveTurn(conversation, "a2");

    expect(toolCalls).toContain("The tool calls in that turn:\n- skill: fleet-code-review\n  <skill_content>…</skill_content>");
    expect(toolCalls).toContain("- bash: git diff\n");
    expect(toolCalls).toContain(`  ${"x".repeat(1500)}\n  …`);
    expect(toolCalls).not.toContain("x".repeat(1501));
  });

  it("is empty for a message it can't find", () => {
    expect(improveTurn(conversation, "missing")).toEqual({ exchange: "", toolCalls: "" });
  });
});

describe("labels", () => {
  it("names the copy sessions get and rounds token counts", () => {
    expect(versionLabel(null)).toBe("Fleet's");
    expect(versionLabel(3)).toBe("Yours · v3");
    expect(formatTokens(380)).toBe("~380 tokens");
    expect(formatTokens(2412)).toBe("~2.4k tokens");
  });
});
