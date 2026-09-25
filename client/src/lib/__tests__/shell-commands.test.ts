import { describe, expect, it } from "vitest";
import type { AccumulatedMessage } from "@/lib/client-types";
import { applyPartUpdate, ensureMessage, mergeMessageUpdate } from "@/lib/event-state";
import { isShellDraft, shellDraftCommand, toMessageRole, toShellCommandView } from "@/lib/shell-commands";

function shellMessage(state: Record<string, unknown>): AccumulatedMessage {
  return {
    messageId: "msg_1",
    sessionId: "session-1",
    role: "shell",
    parts: [{ partId: "msg_1-tool-sh_1", type: "tool", tool: "shell", callId: "sh_1", state }],
  };
}

describe("shell drafts", () => {
  it("is shell mode only when the draft starts with !", () => {
    expect(isShellDraft("!git status")).toBe(true);
    expect(isShellDraft(" !git status")).toBe(false);
    expect(isShellDraft("run !this")).toBe(false);
  });

  it("runs what follows the !", () => {
    expect(shellDraftCommand("!  git status  ")).toBe("git status");
    expect(shellDraftCommand("!")).toBe("");
    expect(shellDraftCommand("git status")).toBe("");
  });
});

describe("toMessageRole", () => {
  it("keeps the user's and the shell's roles and puts everything else on the agent's side", () => {
    expect(toMessageRole("user")).toBe("user");
    expect(toMessageRole("shell")).toBe("shell");
    expect(toMessageRole("assistant")).toBe("assistant");
    expect(toMessageRole("notice")).toBe("assistant");
    expect(toMessageRole(undefined)).toBe("assistant");
  });
});

describe("toShellCommandView", () => {
  it("shows a command still running", () => {
    const view = toShellCommandView(shellMessage({ status: "running", input: { command: "npm test" } }));

    expect(view).toEqual({ id: "msg_1-tool-sh_1", command: "npm test", output: "", state: "running", exit: undefined, truncated: false });
  });

  it("reads how a command ended from its metadata", () => {
    expect(toShellCommandView(shellMessage({
      status: "completed",
      input: { command: "echo hello" },
      output: "hello\n",
      metadata: { exit: 0, status: "exited", truncated: false },
    }))).toMatchObject({ state: "done", exit: 0, output: "hello\n" });

    expect(toShellCommandView(shellMessage({
      status: "completed",
      input: { command: "ls /nope; exit 3" },
      output: "ls: cannot access '/nope'\n",
      metadata: { exit: 3, status: "exited" },
    }))).toMatchObject({ state: "failed", exit: 3 });

    expect(toShellCommandView(shellMessage({
      status: "completed",
      input: { command: "sleep 999" },
      metadata: { status: "timeout", truncated: true },
    }))).toMatchObject({ state: "timeout", truncated: true });
  });

  it("calls a command done when the harness reports no exit code", () => {
    // OpenCode (1.x) keeps the output of a user's command but not its exit code.
    const view = toShellCommandView(shellMessage({
      status: "completed",
      input: { command: "ls /nope; exit 3" },
      output: "ls: cannot access '/nope'\n",
      metadata: { output: "ls: cannot access '/nope'\n" },
    }));

    expect(view).toMatchObject({ state: "done", exit: undefined });
  });

  it("waits for the command's part", () => {
    expect(toShellCommandView({ messageId: "msg_1", sessionId: "session-1", role: "shell", parts: [] })).toBeNull();
  });
});

describe("a shell command arriving live", () => {
  it("becomes the shell's message when its part came before the message", () => {
    // OpenCode (1.x) can send the command's part before the message that says whose it is.
    const partFirst = applyPartUpdate([], {
      messageID: "msg_1",
      sessionID: "session-1",
      id: "prt_1",
      type: "tool",
      tool: "bash",
      callID: "call-1",
      state: { status: "running", input: { command: "git status" } },
    });
    expect(partFirst[0].role).toBe("assistant");

    const updated = mergeMessageUpdate(ensureMessage(partFirst, { id: "msg_1", role: "shell" }), {
      id: "msg_1",
      role: "shell",
      time: { created: 5 },
    });

    expect(updated[0].role).toBe("shell");
    expect(updated[0].parts).toHaveLength(1);
  });

  it("starts as the shell's message when the message comes first", () => {
    const messages = ensureMessage([], { id: "msg_1", role: "shell", sessionID: "session-1", time: { created: 5 } });

    expect(messages[0].role).toBe("shell");
  });
});
