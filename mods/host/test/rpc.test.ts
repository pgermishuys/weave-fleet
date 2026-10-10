import { describe, expect, test } from "bun:test";
import { RpcError, RpcPeer, type RpcPeerOptions } from "../src/rpc";

const enc = new TextEncoder();

/** An input you push chunks into by hand. */
function pipe() {
  const queue: Uint8Array[] = [];
  let wake: (() => void) | null = null;
  let ended = false;
  async function* iterate(): AsyncGenerator<Uint8Array> {
    while (true) {
      const next = queue.shift();
      if (next) yield next;
      else if (ended) return;
      else await new Promise<void>((r) => (wake = r));
    }
  }
  const poke = () => {
    wake?.();
    wake = null;
  };
  return {
    input: iterate(),
    push(data: string | Uint8Array) {
      queue.push(typeof data === "string" ? enc.encode(data) : data);
      poke();
    },
    end() {
      ended = true;
      poke();
    },
  };
}

function setup(options: Partial<RpcPeerOptions> = {}) {
  const p = pipe();
  const lines: string[] = [];
  const logs: string[] = [];
  const peer = new RpcPeer({
    input: p.input,
    write: (l) => void lines.push(l),
    log: (m) => void logs.push(m),
    ...options,
  });
  const done = peer.run();
  const sent = () => lines.map((l) => JSON.parse(l));
  return { peer, p, lines, logs, done, sent };
}

const FAILS = (p: Promise<unknown>): Promise<any> => p.then(() => { throw new Error("expected a rejection"); }, (e) => e);
const tick = (ms = 5) => new Promise((r) => setTimeout(r, ms));
const call = (id: unknown, method: string, params?: unknown) => JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n";

describe("framing", () => {
  test("one line in one chunk is handled", async () => {
    const { peer, p, sent } = setup();
    peer.handle("echo", (x) => x);
    p.push(call(1, "echo", { a: 1 }));
    await tick();
    expect(sent()).toEqual([{ jsonrpc: "2.0", id: 1, result: { a: 1 } }]);
  });

  test("one line split into 1-byte chunks is handled", async () => {
    const { peer, p, sent } = setup();
    peer.handle("echo", (x) => x);
    for (const b of enc.encode(call(2, "echo", "hi"))) p.push(new Uint8Array([b]));
    await tick();
    expect(sent()).toEqual([{ jsonrpc: "2.0", id: 2, result: "hi" }]);
  });

  test("several lines in one chunk are all handled", async () => {
    const { peer, p, sent } = setup();
    peer.handle("echo", (x) => x);
    p.push(call(1, "echo", 1) + call(2, "echo", 2) + call(3, "echo", 3));
    await tick();
    expect(sent().map((m) => m.result)).toEqual([1, 2, 3]);
  });

  test("CRLF line endings work", async () => {
    const { peer, p, sent } = setup();
    peer.handle("echo", (x) => x);
    p.push(call(1, "echo", 1).replace("\n", "\r\n"));
    await tick();
    expect(sent()).toEqual([{ jsonrpc: "2.0", id: 1, result: 1 }]);
  });

  test("a multi-byte character split across chunks survives", async () => {
    const { peer, p, sent } = setup();
    peer.handle("echo", (x) => x);
    const bytes = enc.encode(call(1, "echo", "café \u{1F600}"));
    const cut = bytes.indexOf(0xc3) + 1;
    p.push(bytes.slice(0, cut));
    p.push(bytes.slice(cut, cut + 7));
    p.push(bytes.slice(cut + 7));
    await tick();
    expect(sent()[0].result).toBe("café \u{1F600}");
  });

  test("empty and blank lines are ignored", async () => {
    const { peer, p, sent } = setup();
    peer.handle("echo", (x) => x);
    p.push("\n  \n\r\n" + call(1, "echo", 1) + "\t\n");
    await tick();
    expect(sent()).toHaveLength(1);
  });

  test("an oversized line is answered -32600 and the next line is still handled", async () => {
    const { peer, p, sent } = setup({ maxLineBytes: 100 });
    peer.handle("echo", (x) => x);
    p.push("x".repeat(60));
    p.push("y".repeat(60));
    p.push("z".repeat(60) + "\n");
    p.push(call(1, "echo", 1));
    await tick();
    const out = sent();
    expect(out[0]).toEqual({
      jsonrpc: "2.0",
      id: null,
      error: { code: -32600, message: "line longer than 8 MiB" },
    });
    expect(out[1]).toEqual({ jsonrpc: "2.0", id: 1, result: 1 });
  });

  test("an oversized line inside one chunk is dropped and the rest of the chunk handled", async () => {
    const { peer, p, sent } = setup({ maxLineBytes: 100 });
    peer.handle("echo", (x) => x);
    p.push("x".repeat(500) + "\n" + call(1, "echo", 1));
    await tick();
    expect(sent().map((m) => m.error?.code ?? m.result)).toEqual([-32600, 1]);
  });
});

describe("parsing", () => {
  test("bad JSON is -32700 with a null id", async () => {
    const { p, sent } = setup();
    p.push("{nope\n");
    await tick();
    expect(sent()).toEqual([
      { jsonrpc: "2.0", id: null, error: { code: -32700, message: expect.any(String) } },
    ]);
  });

  test.each([
    ["a number", "42"],
    ["null", "null"],
    ["a string", '"hi"'],
    ["an array", "[1,2]"],
    ["a batch", '[{"jsonrpc":"2.0","id":1,"method":"echo"}]'],
    ["a missing jsonrpc", '{"id":1,"method":"echo"}'],
    ["a wrong jsonrpc", '{"jsonrpc":"1.0","id":1,"method":"echo"}'],
    ["a method that is not a string", '{"jsonrpc":"2.0","id":1,"method":5}'],
    ["a bad id type", '{"jsonrpc":"2.0","id":{"a":1},"method":"echo"}'],
    ["a null id on a request", '{"jsonrpc":"2.0","id":null,"method":"echo"}'],
    ["neither method nor result", '{"jsonrpc":"2.0","id":1}'],
  ])("%s is -32600", async (_name, line) => {
    const { peer, p, sent } = setup();
    peer.handle("echo", (x) => x);
    p.push(line + "\n");
    await tick();
    const out = sent();
    expect(out).toHaveLength(1);
    expect(out[0].error.code).toBe(-32600);
  });

  test("an invalid message with a readable id answers with that id", async () => {
    const { p, sent } = setup();
    p.push('{"id":"abc","method":"echo"}\n');
    await tick();
    expect(sent()[0].id).toBe("abc");
    expect(sent()[0].error.code).toBe(-32600);
  });

  test("an unknown method is -32601 and names the method", async () => {
    const { p, sent } = setup();
    p.push(call(7, "nope.nothing"));
    await tick();
    const out = sent()[0];
    expect(out.id).toBe(7);
    expect(out.error.code).toBe(-32601);
    expect(out.error.message).toContain("nope.nothing");
  });

  test("an unknown notification is ignored without an answer", async () => {
    const { p, lines } = setup();
    p.push(JSON.stringify({ jsonrpc: "2.0", method: "whatever", params: {} }) + "\n");
    await tick();
    expect(lines).toEqual([]);
  });

  test("a known notification reaches its handler and gets no answer", async () => {
    const { peer, p, lines } = setup();
    const seen: unknown[] = [];
    peer.onNotification("log", (x) => void seen.push(x));
    p.push(JSON.stringify({ jsonrpc: "2.0", method: "log", params: { text: "hi" } }) + "\n");
    await tick();
    expect(seen).toEqual([{ text: "hi" }]);
    expect(lines).toEqual([]);
  });

  test("a throwing notification handler is logged and the loop carries on", async () => {
    const { peer, p, logs, sent } = setup();
    peer.onNotification("boom", () => {
      throw new Error("bang");
    });
    peer.handle("echo", (x) => x);
    p.push(JSON.stringify({ jsonrpc: "2.0", method: "boom" }) + "\n" + call(1, "echo", 1));
    await tick();
    expect(logs.join("\n")).toContain("bang");
    expect(sent()).toHaveLength(1);
  });
});

describe("requests in", () => {
  test("the handler's return value is the result", async () => {
    const { peer, p, sent } = setup();
    peer.handle("sum", (x) => x.a + x.b);
    p.push(call(1, "sum", { a: 2, b: 3 }));
    await tick();
    expect(sent()[0].result).toBe(5);
  });

  test("an async handler's resolved value is the result", async () => {
    const { peer, p, sent } = setup();
    peer.handle("later", async () => {
      await tick(2);
      return "ok";
    });
    p.push(call(1, "later"));
    await tick(20);
    expect(sent()[0].result).toBe("ok");
  });

  test("an undefined result is sent as null", async () => {
    const { peer, p, lines } = setup();
    peer.handle("nothing", () => undefined);
    p.push(call(1, "nothing"));
    await tick();
    expect(JSON.parse(lines[0])).toEqual({ jsonrpc: "2.0", id: 1, result: null });
  });

  test("a thrown RpcError keeps its code, message and data", async () => {
    const { peer, p, sent } = setup();
    peer.handle("fail", () => {
      throw new RpcError(-32001, "not loaded", { report: [1] });
    });
    p.push(call(1, "fail"));
    await tick();
    expect(sent()[0].error).toEqual({ code: -32001, message: "not loaded", data: { report: [1] } });
  });

  test("an RpcError without data sends no data field", async () => {
    const { peer, p, sent } = setup();
    peer.handle("fail", async () => {
      throw new RpcError(-32602, "bad params");
    });
    p.push(call(1, "fail"));
    await tick();
    expect(sent()[0].error).toEqual({ code: -32602, message: "bad params" });
  });

  test("any other throw is -32603 with the message", async () => {
    const { peer, p, sent } = setup();
    peer.handle("fail", () => {
      throw new Error("kaboom");
    });
    p.push(call(1, "fail"));
    await tick();
    expect(sent()[0].error).toEqual({ code: -32603, message: "kaboom" });
  });

  test("a throw of a non-Error is still -32603", async () => {
    const { peer, p, sent } = setup();
    peer.handle("fail", () => {
      throw "plain string";
    });
    p.push(call(1, "fail"));
    await tick();
    expect(sent()[0].error.code).toBe(-32603);
  });

  test("handlers run concurrently: a slow one doesn't block a fast one", async () => {
    const { peer, p, sent } = setup();
    let release!: () => void;
    peer.handle("slow", () => new Promise((r) => (release = () => r("slow"))));
    peer.handle("fast", () => "fast");
    p.push(call(1, "slow") + call(2, "fast"));
    await tick();
    expect(sent().map((m) => m.id)).toEqual([2]);
    release();
    await tick();
    expect(sent().map((m) => m.id)).toEqual([2, 1]);
  });

  test("a result over the line limit is answered -32603 result too large", async () => {
    const { peer, p, sent } = setup({ maxLineBytes: 200 });
    peer.handle("big", () => "x".repeat(500));
    p.push(call(1, "big"));
    await tick();
    expect(sent()[0]).toEqual({
      jsonrpc: "2.0",
      id: 1,
      error: { code: -32603, message: "result too large" },
    });
  });

  test("a result that can't be serialised is answered -32603", async () => {
    const { peer, p, sent } = setup();
    peer.handle("big", () => 10n);
    p.push(call(1, "big"));
    await tick();
    expect(sent()[0].error.code).toBe(-32603);
  });

  test("a throwing write is logged and does not stop the loop", async () => {
    let n = 0;
    const { peer, p, logs, lines } = setup({
      write: () => {
        n++;
        if (n === 1) throw new Error("pipe closed");
      },
    });
    peer.handle("echo", (x) => x);
    p.push(call(1, "echo", 1) + call(2, "echo", 2));
    await tick();
    expect(n).toBe(2);
    expect(logs.join("\n")).toContain("pipe closed");
    expect(lines).toEqual([]);
  });
});

describe("requests out", () => {
  test("a request is sent with an increasing numeric id and resolved by its response", async () => {
    const { peer, p, sent } = setup();
    const a = peer.request("store.get", { key: "k" });
    const b = peer.request("store.get", { key: "j" });
    await tick();
    const out = sent();
    expect(out[0]).toEqual({ jsonrpc: "2.0", id: expect.any(Number), method: "store.get", params: { key: "k" } });
    expect(out[1].id).toBeGreaterThan(out[0].id);
    p.push(JSON.stringify({ jsonrpc: "2.0", id: out[1].id, result: "second" }) + "\n");
    p.push(JSON.stringify({ jsonrpc: "2.0", id: out[0].id, result: "first" }) + "\n");
    expect(await a).toBe("first");
    expect(await b).toBe("second");
  });

  test("an error response rejects with code, message and data", async () => {
    const { peer, p, sent } = setup();
    const r = peer.request("store.get", {});
    await tick();
    p.push(
      JSON.stringify({ jsonrpc: "2.0", id: sent()[0].id, error: { code: -32000, message: "nope", data: { x: 1 } } }) +
        "\n",
    );
    const err = await FAILS(r);
    expect(err).toBeInstanceOf(RpcError);
    expect(err.code).toBe(-32000);
    expect(err.message).toBe("nope");
    expect(err.data).toEqual({ x: 1 });
  });

  test("a request past its timeout rejects -32603 and a late answer is ignored", async () => {
    const { peer, p, sent, logs } = setup({ requestTimeoutMs: 20 });
    const r = peer.request("slow", {});
    const err = await FAILS(r);
    expect(err).toBeInstanceOf(RpcError);
    expect(err.code).toBe(-32603);
    expect(err.message).toContain("timed out after 20 ms");
    p.push(JSON.stringify({ jsonrpc: "2.0", id: sent()[0].id, result: 1 }) + "\n");
    await tick();
    expect(sent()).toHaveLength(1);
    expect(logs.length).toBeGreaterThan(0);
  });

  test("a per-call timeout beats the default", async () => {
    const { peer } = setup({ requestTimeoutMs: 10_000 });
    const err = await FAILS(peer.request("slow", {}, { timeoutMs: 10 }));
    expect(err.message).toContain("timed out after 10 ms");
  });

  test("a response with an unknown id is logged and ignored", async () => {
    const { p, lines, logs } = setup();
    p.push(JSON.stringify({ jsonrpc: "2.0", id: 999, result: 1 }) + "\n");
    await tick();
    expect(lines).toEqual([]);
    expect(logs.join("\n")).toContain("999");
  });

  test("an error response with a null id is logged and never answered", async () => {
    const { p, lines } = setup();
    p.push(JSON.stringify({ jsonrpc: "2.0", id: null, error: { code: -32700, message: "x" } }) + "\n");
    await tick();
    expect(lines).toEqual([]);
  });

  test("notify sends a message without an id", async () => {
    const { peer, lines } = setup();
    peer.notify("invalidate", { mod: "test-chips" });
    expect(JSON.parse(lines[0])).toEqual({ jsonrpc: "2.0", method: "invalidate", params: { mod: "test-chips" } });
  });

  test("an oversize outgoing request rejects and sends nothing", async () => {
    const { peer, lines } = setup({ maxLineBytes: 100 });
    const err = await FAILS(peer.request("big", "x".repeat(500)));
    expect(err).toBeInstanceOf(RpcError);
    expect(lines).toEqual([]);
  });

  test("an oversize outgoing notification is logged and dropped", async () => {
    const { peer, lines, logs } = setup({ maxLineBytes: 100 });
    peer.notify("big", "x".repeat(500));
    expect(lines).toEqual([]);
    expect(logs.length).toBeGreaterThan(0);
  });

  test("an unserialisable request rejects, an unserialisable notification is dropped", async () => {
    const { peer, lines, logs } = setup();
    const cyclic: any = {};
    cyclic.self = cyclic;
    expect(await FAILS(peer.request("x", cyclic))).toBeInstanceOf(RpcError);
    peer.notify("x", 1n);
    expect(lines).toEqual([]);
    expect(logs.length).toBeGreaterThan(0);
  });

  test("a throwing write rejects the request", async () => {
    const { peer } = setup({
      write: () => {
        throw new Error("pipe closed");
      },
    });
    const err = await FAILS(peer.request("x", {}));
    expect(err).toBeInstanceOf(RpcError);
    expect(err.message).toContain("pipe closed");
  });
});

describe("the end", () => {
  test("input ending rejects pending requests and run() resolves", async () => {
    const { peer, p, done } = setup();
    const r = peer.request("x", {});
    p.end();
    const err = await FAILS(r);
    expect(err).toBeInstanceOf(RpcError);
    expect(err.message).toContain("connection closed");
    await done;
  });

  test("a request after the end rejects at once", async () => {
    const { peer, p, done, lines } = setup();
    p.end();
    await done;
    const err = await FAILS(peer.request("x", {}));
    expect(err.message).toContain("connection closed");
    expect(lines).toEqual([]);
  });

  test("a final line without a newline is dropped at the end", async () => {
    const { peer, p, done, lines } = setup();
    peer.handle("echo", (x) => x);
    p.push(call(1, "echo", 1).trimEnd());
    p.end();
    await done;
    expect(lines).toEqual([]);
  });

  test("input that throws ends the run and rejects pending requests", async () => {
    async function* broken(): AsyncGenerator<Uint8Array> {
      await tick(2);
      throw new Error("stream broke");
    }
    const lines: string[] = [];
    const logs: string[] = [];
    const peer = new RpcPeer({ input: broken(), write: (l) => void lines.push(l), log: (m) => void logs.push(m) });
    const done = peer.run();
    const r = peer.request("x", {});
    await done;
    expect((await FAILS(r)).message).toContain("connection closed");
    expect(logs.join("\n")).toContain("stream broke");
  });
});

describe("two peers", () => {
  test("two peers wired together call each other both ways", async () => {
    const intoA = pipe();
    const intoB = pipe();
    const a = new RpcPeer({ input: intoA.input, write: (l) => intoB.push(l + "\n") });
    const b = new RpcPeer({ input: intoB.input, write: (l) => intoA.push(l + "\n") });
    a.handle("a.double", (n) => n * 2);
    b.handle("b.greet", async (name) => `hello ${name}`);
    b.handle("b.fail", () => {
      throw new RpcError(-32001, "nope", { why: "test" });
    });
    const seen: unknown[] = [];
    a.onNotification("ping", (x) => void seen.push(x));
    const runs = [a.run(), b.run()];

    expect(await b.request("a.double", 21)).toBe(42);
    expect(await a.request("b.greet", "fleet")).toBe("hello fleet");
    const err = await FAILS(a.request("b.fail", {}));
    expect(err.code).toBe(-32001);
    expect(err.data).toEqual({ why: "test" });
    const missing = await FAILS(a.request("b.missing", {}));
    expect(missing.code).toBe(-32601);
    b.notify("ping", { n: 1 });
    await tick();
    expect(seen).toEqual([{ n: 1 }]);

    intoA.end();
    intoB.end();
    await Promise.all(runs);
  });
});
