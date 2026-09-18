import { describe, expect, it } from "vitest";
import { parsePeerMessage } from "@/lib/session-messages";

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
