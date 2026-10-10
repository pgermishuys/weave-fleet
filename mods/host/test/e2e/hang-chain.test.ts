// With several mods in the chain the host can't tell Fleet which one hung, so it says which one is running before each
// hook starts (the `running` notification). A busy loop never yields, so the line has to be on the pipe already.
import { expect, test } from "bun:test";
import { join } from "node:path";

const HOST_DIR = join(import.meta.dir, "..", "..");
const fixture = (name: string) => join(import.meta.dir, "..", "fixtures/hang", name);

test("a hung mod behind another: the last running line names the hung mod and the dispatch is never answered", async () => {
  const proc = Bun.spawn([process.execPath, "src/main.ts", "--stdio"], { cwd: HOST_DIR, stdin: "pipe", stdout: "pipe", stderr: "pipe", env: {} });
  const lines: string[] = [];
  try {
    void (async () => {
      let buffer = "";
      for await (const chunk of proc.stdout as ReadableStream<Uint8Array>) {
        buffer += new TextDecoder().decode(chunk);
        let nl: number;
        while ((nl = buffer.indexOf("\n")) >= 0) {
          lines.push(buffer.slice(0, nl));
          buffer = buffer.slice(nl + 1);
        }
      }
    })();
    const send = (id: number, method: string, params: unknown) => proc.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
    const until = async (cond: () => boolean) => {
      const end = Date.now() + 5000;
      while (!cond() && Date.now() < end) await Bun.sleep(10);
    };
    const answered = (id: number) => lines.some((l) => JSON.parse(l).id === id);

    send(1, "initialize", { protocol: 1, fleetVersion: "test" });
    send(2, "load", { id: "hang-outer@v1", name: "hang-outer", version: 1, root: fixture("hang-outer") });
    send(3, "load", { id: "hang-inner@v1", name: "hang-inner", version: 1, root: fixture("hang-inner") });
    await proc.stdin.flush();
    await until(() => answered(3));
    expect(answered(2)).toBe(true);
    expect(answered(3)).toBe(true);

    const e = { component: "ComposerBand", sessionId: "ses_test1", requestId: "ses_test1", props: { isWorking: false } };
    send(4, "dispatch", { event: "ui.render", sessionId: "ses_test1", mods: ["hang-outer@v1", "hang-inner@v1"], e });
    await proc.stdin.flush();
    await until(() => lines.filter((l) => JSON.parse(l).method === "running").length >= 2);
    await Bun.sleep(500);

    const running = lines.filter((l) => JSON.parse(l).method === "running");
    console.error("last running line on the wire:", running.at(-1));
    expect(running.map((l) => JSON.parse(l).params.mod)).toEqual(["hang-outer@v1", "hang-inner@v1"]);
    expect(JSON.parse(running.at(-1)!).params).toEqual({ mod: "hang-inner@v1", event: "ui.render", sessionId: "ses_test1" });
    expect(answered(4)).toBe(false);
    expect(proc.exitCode).toBeNull();
  } finally {
    proc.kill("SIGKILL");
  }
  expect(await proc.exited).not.toBe(0);
});
