import { describe, expect, test } from "bun:test";
import { setup } from "../helpers/harness";

const done = { sessionId: "ses_test1", turnId: "msg_1", isAborted: false, isFailed: false, changedFiles: ["src/berths.ts"] };
const ids = (...names: string[]) => names.map((n) => `${n}@v1`);

describe("turn.complete and $.prompt.submit", () => {
  test("a turn.complete hook that calls $.prompt.submit sends prompt.queue with the text", async () => {
    const s = setup();
    s.peer.answers["prompt.queue"] = () => ({});
    await s.load("test-follow", `on("turn.complete", async ($, e, next) => { await $.prompt.submit({ text: "Run the tests" }); return next(e); });`);
    const r = await s.dispatch("turn.complete", ids("test-follow"), done);
    expect(r.failures).toEqual([]);
    expect(s.peer.reqs("prompt.queue")).toEqual([{ mod: "test-follow@v1", sessionId: "ses_test1", text: "Run the tests" }]);
  });

  test("Fleet's refusal rejects in the mod with Fleet's reason", async () => {
    const s = setup();
    s.peer.answers["prompt.queue"] = () => {
      throw new Error("This mod has queued 3 follow-ups in a row; Fleet waits for you before another.");
    };
    await s.load("test-refused", `on("turn.complete", async ($, e, next) => { try { await $.prompt.submit({ text: "again" }); } catch (err) { $.ui.log("refused: " + err.message); } return next(e); });`);
    const r = await s.dispatch("turn.complete", ids("test-refused"), done);
    expect(r.failures).toEqual([]);
    expect(s.peer.notes("log").map((l) => l.text)).toEqual(["refused: This mod has queued 3 follow-ups in a row; Fleet waits for you before another."]);
  });

  test("empty text, or no text, throws in the mod before anything is sent", async () => {
    const s = setup();
    await s.load("test-empty", `on("turn.complete", async ($, e, next) => { await $.prompt.submit({ text: "  " }); return next(e); });`);
    const r = await s.dispatch("turn.complete", ids("test-empty"), done);
    expect(r.failures).toMatchObject([{ mod: "test-empty@v1", kind: "throw" }]);
    expect(s.peer.reqs("prompt.queue")).toEqual([]);
  });
});
