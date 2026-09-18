import { describe, expect, it } from "vitest";
import { parsePeerMessage, parsePeerUpdate } from "@/lib/session-messages";

describe("parsePeerMessage", () => {
  it("returns the sender and the text of a message another session sent", () => {
    const body = '<fleet-session-message from="ses_1" title="Fix authentication flow">\nUpdate the docs.\n</fleet-session-message>';

    expect(parsePeerMessage(body)).toEqual({
      peer: { sessionId: "ses_1", title: "Fix authentication flow" },
      text: "Update the docs.",
    });
  });

  it("keeps the text's own lines and decodes the title Fleet encoded", () => {
    const body = '<fleet-session-message from="ses_1" title="Say &quot;hi&quot; &amp; &lt;go&gt;">\nOne.\n\nTwo.\n</fleet-session-message>';

    expect(parsePeerMessage(body)).toEqual({
      peer: { sessionId: "ses_1", title: 'Say "hi" & <go>' },
      text: "One.\n\nTwo.",
    });
  });

  it("ignores a prompt that only mentions the tag", () => {
    expect(parsePeerMessage("Why does <fleet-session-message> show up here?")).toBeNull();
    expect(parsePeerMessage('Look: <fleet-session-message from="a" title="b">\nx\n</fleet-session-message>')).toBeNull();
  });
});

describe("parsePeerUpdate", () => {
  it("returns the session, how its turn ended and its reply", () => {
    const body = '<fleet-session-update session="ses_2" title="Update documentation" outcome="finished">\nDocs updated.\n</fleet-session-update>';

    expect(parsePeerUpdate(body)).toEqual({
      peer: { sessionId: "ses_2", title: "Update documentation" },
      outcome: "finished",
      text: "Docs updated.",
    });
  });

  it("reads a failure and decodes the title", () => {
    const body = '<fleet-session-update session="ses_2" title="Docs &amp; tests" outcome="failed">\nRate limited.\n</fleet-session-update>';

    expect(parsePeerUpdate(body)).toEqual({
      peer: { sessionId: "ses_2", title: "Docs & tests" },
      outcome: "failed",
      text: "Rate limited.",
    });
  });

  it("is not a message, and a message is not an update", () => {
    const message = '<fleet-session-message from="ses_1" title="Fix authentication flow">\nHi.\n</fleet-session-message>';
    const update = '<fleet-session-update session="ses_2" title="Docs" outcome="finished">\nDone.\n</fleet-session-update>';

    expect(parsePeerUpdate(message)).toBeNull();
    expect(parsePeerMessage(update)).toBeNull();
    expect(parsePeerUpdate('<fleet-session-update session="a" title="b" outcome="maybe">\nx\n</fleet-session-update>')).toBeNull();
  });
});
