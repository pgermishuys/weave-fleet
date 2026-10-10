import { join } from "node:path";

const HOST_DIR = join(import.meta.dir, "..", "..");

type Pending = { resolve: (v: any) => void; reject: (e: any) => void };

/** A fake Fleet: spawns the real host and speaks JSON-RPC to it over stdin and stdout. */
export class HostClient {
  proc: ReturnType<typeof Bun.spawn>;
  /** Every stdout line, parsed (or raw when it isn't JSON). */
  lines: unknown[] = [];
  rawStdout = "";
  stderr = "";
  notifications: { method: string; params: any }[] = [];
  requestsFromHost: { method: string; params: any }[] = [];
  /** How this fake Fleet answers the host's requests. */
  answers: Record<string, (params: any) => unknown> = {
    "store.get": () => ({ value: "stored" }),
    "store.set": () => ({}),
    "session.get": (p) => ({ id: p.sessionId, title: "Invented title", harness: "opencode", cwd: "/work/demo", surfaces: ["desktop"] }),
  };
  private pending = new Map<number, Pending>();
  private nextId = 1;
  exited: Promise<number>;

  constructor(args: string[] = ["src/main.ts", "--stdio"]) {
    this.proc = Bun.spawn([process.execPath, ...args], { cwd: HOST_DIR, stdin: "pipe", stdout: "pipe", stderr: "pipe", env: {} });
    void this.readStdout();
    void this.readStderr();
    this.exited = this.proc.exited;
  }

  private async readStdout() {
    const decoder = new TextDecoder();
    let buffer = "";
    for await (const chunk of this.proc.stdout as ReadableStream<Uint8Array>) {
      const text = decoder.decode(chunk, { stream: true });
      this.rawStdout += text;
      buffer += text;
      let nl: number;
      while ((nl = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, nl);
        buffer = buffer.slice(nl + 1);
        this.onLine(line);
      }
    }
  }
  private async readStderr() {
    const decoder = new TextDecoder();
    for await (const chunk of this.proc.stderr as ReadableStream<Uint8Array>) this.stderr += decoder.decode(chunk, { stream: true });
  }

  private onLine(line: string) {
    let msg: any;
    try {
      msg = JSON.parse(line);
    } catch {
      this.lines.push(line);
      return;
    }
    this.lines.push(msg);
    if (msg.method && msg.id !== undefined) {
      this.requestsFromHost.push({ method: msg.method, params: msg.params });
      const answer = this.answers[msg.method];
      const reply = answer ? { jsonrpc: "2.0", id: msg.id, result: answer(msg.params) } : { jsonrpc: "2.0", id: msg.id, error: { code: -32601, message: "no answer" } };
      this.sendRaw(JSON.stringify(reply));
    } else if (msg.method) {
      this.notifications.push({ method: msg.method, params: msg.params });
    } else if (typeof msg.id === "number" && this.pending.has(msg.id)) {
      const p = this.pending.get(msg.id)!;
      this.pending.delete(msg.id);
      if (msg.error) p.reject(Object.assign(new Error(msg.error.message), msg.error));
      else p.resolve(msg.result);
    }
  }

  sendRaw(line: string) {
    (this.proc.stdin as any).write(`${line}\n`);
    (this.proc.stdin as any).flush?.();
  }

  request(method: string, params: unknown = {}): Promise<any> {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      this.pending.set(id, { resolve, reject });
      this.sendRaw(JSON.stringify({ jsonrpc: "2.0", id, method, params }));
    });
  }

  notes(method: string): any[] {
    return this.notifications.filter((n) => n.method === method).map((n) => n.params);
  }

  closeStdin() {
    (this.proc.stdin as any).end();
  }

  kill() {
    this.proc.kill();
  }
}
