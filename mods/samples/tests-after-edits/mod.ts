import type { Register } from "fleet-mods";

// When a turn ends having changed files (and was neither stopped nor failed), "Run the tests" is queued. It shows in
// the queue list and is sent next. Fleet queues at most 3 follow-ups in a row before it waits for you.
export const register: Register = (on) => {
  on("turn.complete", async ($, e, next) => {
    if (e.changedFiles.length > 0 && !e.isAborted && !e.isFailed) {
      try {
        await $.prompt.submit({ text: "Run the tests" });
      } catch (err) {
        $.ui.log(`Could not queue the tests: ${err instanceof Error ? err.message : String(err)}`, { level: "warn" });
      }
    }
    return next(e);
  });
};
