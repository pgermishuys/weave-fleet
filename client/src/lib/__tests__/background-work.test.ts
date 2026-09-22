import { describe, expect, it } from "vitest";
import type { AccumulatedToolPart } from "@/lib/client-types";
import { backgroundWorkId, finishedBackgroundWork, parseBackgroundNotice } from "@/lib/background-work";
import { toToolCardItem } from "@/components/session/activity-stream-tool-card";

/** What OpenCode 2 (2.0.9) really sends: a background shell's result, and the notice when it finishes. */
const shell_result = {
  status: "running",
  background: true,
  input: { command: "sleep 6; echo done", description: "Slow job", background: true },
  output: "Command moved to the background (shell ID: sh_1).\nOutput is streaming to: /tmp/sh_1.out",
  metadata: { status: "running", truncated: false, shellID: "sh_1" },
};

const shell_notice = `<shell id="sh_1" state="completed" command="sleep 6; echo done">
done

Command exited with code 0.
</shell>`;

const subagent_notice = `<subagent sessionID="ses_child" state="completed" description="Slow helper">
The child finished its slow task.
</subagent>`;

function tool_part(state: unknown, tool = "shell"): AccumulatedToolPart {
  return { partId: "part-1", type: "tool", tool, callId: "call-1", state };
}

describe("parseBackgroundNotice", () => {
  it("reads a finished shell", () => {
    expect(parseBackgroundNotice(shell_notice)).toEqual({
      kind: "shell",
      id: "sh_1",
      state: "completed",
      label: "sleep 6; echo done",
      text: "done\n\nCommand exited with code 0.",
    });
  });

  it("reads a finished subagent, and how it ended", () => {
    expect(parseBackgroundNotice(subagent_notice)).toMatchObject({
      kind: "subagent",
      id: "ses_child",
      state: "completed",
      label: "Slow helper",
      text: "The child finished its slow task.",
    });
    expect(parseBackgroundNotice(subagent_notice.replace('state="completed"', 'state="error"'))?.state).toBe("error");
    expect(parseBackgroundNotice(subagent_notice.replace('state="completed"', 'state="cancelled"'))?.state).toBe("cancelled");
  });

  it("leaves an ordinary prompt alone", () => {
    expect(parseBackgroundNotice("Please run the tests")).toBeNull();
    expect(parseBackgroundNotice("<shell> what does this tag do?")).toBeNull();
  });
});

describe("a backgrounded call's card", () => {
  it("says Background while the work runs, although the call itself succeeded", () => {
    const card = toToolCardItem(tool_part(shell_result));

    expect(card.status).toBe("Background");
    expect(card.preview).toContain("moved to the background");
  });

  it("is finished by the notice that names its shell", () => {
    const finished = finishedBackgroundWork([shell_notice]);

    expect(toToolCardItem(tool_part(shell_result), finished).status).toBe("Completed");
    expect(toToolCardItem(tool_part(shell_result), finishedBackgroundWork([])).status).toBe("Background");
  });

  it("is finished by the notice that names its child session", () => {
    const call = tool_part({
      status: "running",
      background: true,
      input: { description: "Slow helper", background: true },
      metadata: { sessionID: "ses_child", status: "running", truncated: false },
    }, "subagent");

    expect(toToolCardItem(call).status).toBe("Background");
    expect(toToolCardItem(call, finishedBackgroundWork([subagent_notice])).status).toBe("Completed");
    expect(toToolCardItem(call, finishedBackgroundWork([subagent_notice.replace('state="completed"', 'state="error"')])).status).toBe("Error");
  });

  it("leaves an ordinary call alone", () => {
    const done = tool_part({ status: "completed", output: "hi", metadata: { status: "completed", exit: 0 } });

    expect(backgroundWorkId(done.state)).toBeNull();
    expect(toToolCardItem(done, finishedBackgroundWork([shell_notice])).status).toBe("Completed");
  });

  it("leaves a subagent that is merely running alone", () => {
    // A foreground subagent reports its child session with the same "status": "running" metadata, so only the
    // call having returned tells them apart.
    const working = tool_part({ status: "running", input: {}, metadata: { sessionID: "ses_child", status: "running" } }, "subagent");

    expect(backgroundWorkId(working.state)).toBeNull();
    expect(toToolCardItem(working, finishedBackgroundWork([subagent_notice])).status).toBe("Running");
  });
});
