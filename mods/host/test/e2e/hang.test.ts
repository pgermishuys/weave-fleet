// What Fleet's supervisor (M2) gets when a hook never yields: the host can't stop it, so it stops answering, and it
// doesn't crash or exit by itself. Fleet's 15 s wait for a dispatch, then a restart, is the bound (docs/mods/api.md,
// "A blocked host").
import { expect, test } from "bun:test";
import { join } from "node:path";

const HOST_DIR = join(import.meta.dir, "..", "..");
const ROOT = join(import.meta.dir, "..", "fixtures/hang/hang-hook");

test("a hook that never yields: the dispatch isn't answered and the host stays up until it is killed", async () => {
  const proc = Bun.spawn([process.execPath, "src/main.ts", "--stdio"], { cwd: HOST_DIR, stdin: "pipe", stdout: "pipe", stderr: "pipe", env: {} });
  try {
    await drive(proc);
  } finally {
    proc.kill("SIGKILL");
  }
  expect(await proc.exited).not.toBe(0);
});

async function drive(proc: Bun.Subprocess<"pipe", "pipe", "pipe">) {
  const answered = new Set<number>();
  void (async () => {
    let buffer = "";
    for await (const chunk of proc.stdout as ReadableStream<Uint8Array>) {
      buffer += new TextDecoder().decode(chunk);
      let nl: number;
      while ((nl = buffer.indexOf("\n")) >= 0) {
        const msg = JSON.parse(buffer.slice(0, nl));
        buffer = buffer.slice(nl + 1);
        if (typeof msg.id === "number" && !msg.method) answered.add(msg.id);
      }
    }
  })();
  const send = (id: number, method: string, params: unknown) => proc.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n");
  send(1, "initialize", { protocol: 1, fleetVersion: "test" });
  send(2, "load", { id: "hang-hook@v1", name: "hang-hook", version: 1, root: ROOT });
  await proc.stdin.flush();
  const until = async (cond: () => boolean) => {
    const end = Date.now() + 5000;
    while (!cond() && Date.now() < end) await Bun.sleep(10);
  };
  await until(() => answered.has(2));
  expect(answered.has(2)).toBe(true);

  const e = { component: "ComposerBand", sessionId: "ses_test1", requestId: "ses_test1", props: { isWorking: false } };
  send(3, "dispatch", { event: "ui.render", sessionId: "ses_test1", mods: ["hang-hook@v1"], e });
  await proc.stdin.flush();
  await Bun.sleep(300);
  // Sent once the hook is looping: nothing else is answered either.
  send(4, "initialize", { protocol: 1, fleetVersion: "test" });
  await proc.stdin.flush();
  await Bun.sleep(1500);
  expect(answered.has(3)).toBe(false);
  expect(answered.has(4)).toBe(false);
  expect(proc.exitCode).toBeNull();
}
