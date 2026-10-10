/**
 * JSON-RPC 2.0 over a line stream (docs/mods/api.md, "The protocol"): one JSON object per line, UTF-8, at most
 * `lineBytes` per line, requests, responses and notifications in both directions.
 */

/** Standard and Fleet-specific error codes. */
export const ErrorCodes = {
  parse: -32700,
  invalidRequest: -32600,
  methodNotFound: -32601,
  invalidParams: -32602,
  internal: -32603,
  /** `initialize` with a protocol the host doesn't speak. */
  protocol: -32000,
  /** `load` of a mod that doesn't load; `data` is the CheckReport. */
  notLoaded: -32001,
} as const;

/** Throw from a handler to answer with this error. */
export class RpcError extends Error {
  constructor(
    readonly code: number,
    message: string,
    readonly data?: unknown,
  ) {
    super(message);
  }
}

export interface RpcPeerOptions {
  /** Bytes from the other side (the host's stdin). */
  input: AsyncIterable<Uint8Array>;
  /** Writes one line, without its newline, to the other side (the host's stdout). */
  write: (line: string) => void;
  /** Longest line either way, in bytes. Default 8 MiB. */
  maxLineBytes?: number;
  /** Default wait for an answer to `request`. Default 10 s. */
  requestTimeoutMs?: number;
  /** Diagnostics (stderr in the real host). */
  log?: (message: string) => void;
}

export type RequestHandler = (params: any) => unknown | Promise<unknown>;
export type NotificationHandler = (params: any) => void;

interface Pending {
  resolve: (value: unknown) => void;
  reject: (error: RpcError) => void;
  timer: ReturnType<typeof setTimeout>;
}

type Id = string | number;

const NEWLINE = 0x0a;
const encoder = new TextEncoder();
const decoder = new TextDecoder("utf-8");
const isObject = (v: unknown): v is Record<string, unknown> => typeof v === "object" && v !== null && !Array.isArray(v);
const isId = (v: unknown): v is Id => typeof v === "string" || (typeof v === "number" && Number.isFinite(v));
const messageOf = (e: unknown) => (e instanceof Error ? e.message : String(e));

export class RpcPeer {
  private readonly options: RpcPeerOptions;
  private readonly maxLineBytes: number;
  private readonly requestTimeoutMs: number;
  private readonly handlers = new Map<string, RequestHandler>();
  private readonly notificationHandlers = new Map<string, NotificationHandler>();
  private readonly pending = new Map<Id, Pending>();
  private nextId = 1;
  private closed = false;

  constructor(options: RpcPeerOptions) {
    this.options = options;
    this.maxLineBytes = options.maxLineBytes ?? 8 * 1024 * 1024;
    this.requestTimeoutMs = options.requestTimeoutMs ?? 10_000;
  }

  /** Answers requests for `method`. A thrown RpcError becomes that error; any other throw becomes -32603. A missing result is sent as `null`. */
  handle(method: string, handler: RequestHandler): void {
    this.handlers.set(method, handler);
  }

  /** Receives notifications for `method`; unknown notifications are ignored. */
  onNotification(method: string, handler: NotificationHandler): void {
    this.notificationHandlers.set(method, handler);
  }

  /** Sends a request and resolves to its result; rejects with RpcError on an error answer or on timeout. */
  request(method: string, params: unknown, options?: { timeoutMs?: number }): Promise<unknown> {
    if (this.closed) return Promise.reject(new RpcError(ErrorCodes.internal, "connection closed"));
    const id = this.nextId++;
    const line = this.serialise({ jsonrpc: "2.0", id, method, params });
    if (typeof line !== "string") return Promise.reject(new RpcError(ErrorCodes.internal, line.problem));
    const timeoutMs = options?.timeoutMs ?? this.requestTimeoutMs;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new RpcError(ErrorCodes.internal, `${method} timed out after ${timeoutMs} ms`));
      }, timeoutMs);
      this.pending.set(id, { resolve, reject, timer });
      try {
        this.options.write(line);
      } catch (e) {
        clearTimeout(timer);
        this.pending.delete(id);
        this.log(`write failed: ${messageOf(e)}`);
        reject(new RpcError(ErrorCodes.internal, `write failed: ${messageOf(e)}`));
      }
    });
  }

  notify(method: string, params: unknown): void {
    const line = this.serialise({ jsonrpc: "2.0", method, params });
    if (typeof line !== "string") {
      this.log(`dropped notification ${method}: ${line.problem}`);
      return;
    }
    this.send(line);
  }

  /** Reads `input` until it ends. Resolves then; pending requests are rejected. */
  async run(): Promise<void> {
    const parts: Uint8Array[] = [];
    let buffered = 0;
    let discarding = false;
    const max = this.maxLineBytes;
    try {
      for await (const chunk of this.options.input) {
        let start = 0;
        while (start < chunk.length) {
          const nl = chunk.indexOf(NEWLINE, start);
          const end = nl === -1 ? chunk.length : nl;
          if (!discarding) {
            if (buffered + (end - start) > max) {
              discarding = true;
              parts.length = 0;
              buffered = 0;
            } else if (end > start) {
              parts.push(chunk.slice(start, end));
              buffered += end - start;
            }
          }
          if (nl === -1) break;
          if (discarding) {
            discarding = false;
            this.answerError(null, ErrorCodes.invalidRequest, "line longer than 8 MiB");
          } else {
            const line = join(parts, buffered);
            parts.length = 0;
            buffered = 0;
            this.onLine(decoder.decode(line));
          }
          start = nl + 1;
        }
      }
    } catch (e) {
      this.log(`input failed: ${messageOf(e)}`);
    }
    this.closed = true;
    for (const [id, p] of [...this.pending]) {
      clearTimeout(p.timer);
      this.pending.delete(id);
      p.reject(new RpcError(ErrorCodes.internal, "connection closed"));
    }
  }

  private onLine(text: string): void {
    if (text.trim() === "") return;
    let msg: unknown;
    try {
      msg = JSON.parse(text);
    } catch {
      this.answerError(null, ErrorCodes.parse, "parse error: line is not valid JSON");
      return;
    }
    if (!isObject(msg) || msg.jsonrpc !== "2.0") {
      this.answerError(isObject(msg) && isId(msg.id) ? msg.id : null, ErrorCodes.invalidRequest, "not a JSON-RPC 2.0 message");
      return;
    }
    const hasId = "id" in msg;
    if (hasId && !isId(msg.id) && !(msg.id === null && !("method" in msg))) {
      this.answerError(null, ErrorCodes.invalidRequest, "id must be a string or a number");
      return;
    }
    if ("method" in msg) {
      if (typeof msg.method !== "string") {
        this.answerError(hasId ? (msg.id as Id) : null, ErrorCodes.invalidRequest, "method must be a string");
      } else if (hasId) {
        this.onRequest(msg.id as Id, msg.method, msg.params);
      } else {
        this.onNotificationIn(msg.method, msg.params);
      }
      return;
    }
    if (hasId && ("result" in msg || "error" in msg)) {
      this.onResponse(msg);
      return;
    }
    this.answerError(isId(msg.id) ? msg.id : null, ErrorCodes.invalidRequest, "not a request, notification or response");
  }

  private onRequest(id: Id, method: string, params: unknown): void {
    const handler = this.handlers.get(method);
    if (!handler) {
      this.answerError(id, ErrorCodes.methodNotFound, `method not found: ${method}`);
      return;
    }
    void (async () => {
      try {
        const result = await handler(params);
        const line = this.serialise({ jsonrpc: "2.0", id, result: result === undefined ? null : result });
        if (typeof line !== "string") {
          this.log(`${method}: ${line.problem}`);
          this.answerError(id, ErrorCodes.internal, line.tooLarge ? "result too large" : line.problem);
        } else {
          this.send(line);
        }
      } catch (e) {
        if (e instanceof RpcError) this.answerError(id, e.code, e.message, e.data);
        else this.answerError(id, ErrorCodes.internal, messageOf(e));
      }
    })();
  }

  private onNotificationIn(method: string, params: unknown): void {
    const handler = this.notificationHandlers.get(method);
    if (!handler) return;
    try {
      const result: unknown = handler(params);
      if (result instanceof Promise) result.catch((e) => this.log(`notification ${method} failed: ${messageOf(e)}`));
    } catch (e) {
      this.log(`notification ${method} failed: ${messageOf(e)}`);
    }
  }

  private onResponse(msg: Record<string, unknown>): void {
    const pending = isId(msg.id) ? this.pending.get(msg.id) : undefined;
    if (!pending) {
      this.log(`ignored a response with no waiting request (id ${JSON.stringify(msg.id)})`);
      return;
    }
    clearTimeout(pending.timer);
    this.pending.delete(msg.id as Id);
    if ("error" in msg) {
      const e = msg.error;
      if (isObject(e) && typeof e.code === "number" && typeof e.message === "string") {
        pending.reject(new RpcError(e.code, e.message, e.data));
      } else {
        pending.reject(new RpcError(ErrorCodes.internal, "malformed error answer", e));
      }
    } else {
      pending.resolve(msg.result);
    }
  }

  private answerError(id: Id | null, code: number, message: string, data?: unknown): void {
    const error: Record<string, unknown> = { code, message };
    if (data !== undefined) error.data = data;
    const line = this.serialise({ jsonrpc: "2.0", id, error });
    if (typeof line === "string") this.send(line);
    else this.send(JSON.stringify({ jsonrpc: "2.0", id, error: { code, message } }));
  }

  /** The JSON line for `value`, or why there isn't one. */
  private serialise(value: unknown): string | { problem: string; tooLarge?: true } {
    let line: string | undefined;
    try {
      line = JSON.stringify(value);
    } catch (e) {
      return { problem: `can't serialise: ${messageOf(e)}` };
    }
    if (typeof line !== "string") return { problem: "can't serialise" };
    if (encoder.encode(line).length > this.maxLineBytes) return { problem: "line too large", tooLarge: true };
    return line;
  }

  private send(line: string): void {
    try {
      this.options.write(line);
    } catch (e) {
      this.log(`write failed: ${messageOf(e)}`);
    }
  }

  private log(message: string): void {
    try {
      this.options.log?.(message);
    } catch {
      // diagnostics must never throw
    }
  }
}

function join(parts: Uint8Array[], length: number): Uint8Array {
  if (parts.length === 1) return parts[0]!;
  const out = new Uint8Array(length);
  let at = 0;
  for (const p of parts) {
    out.set(p, at);
    at += p.length;
  }
  return out;
}
