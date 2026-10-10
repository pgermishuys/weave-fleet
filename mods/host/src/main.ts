/**
 * Entry point: `bun src/main.ts --stdio`. JSON-RPC lines on stdin and stdout; everything else goes to stderr.
 */
import { createHost } from "./host";
import { RpcPeer } from "./rpc";

if (!process.argv.includes("--stdio")) {
  process.stderr.write("usage: bun host.js --stdio\n(speaks JSON-RPC 2.0 over stdin and stdout: docs/mods/api.md, \"The protocol\")\n");
  process.exit(2);
}

// stdout is the protocol. Host code's console goes to stderr; mod code's is routed to its log by the host.
const toStderr = (...args: unknown[]) => void process.stderr.write(`${args.map((a) => (typeof a === "string" ? a : Bun.inspect(a))).join(" ")}\n`);
for (const m of ["log", "info", "debug", "warn", "error"] as const) console[m] = toStderr;

process.on("unhandledRejection", (reason) => toStderr("unhandled rejection:", reason));
process.on("uncaughtException", (error) => toStderr("uncaught exception:", error));

const log = (message: string) => void process.stderr.write(`[mods-host] ${message}\n`);
const peer = new RpcPeer({
  input: Bun.stdin.stream() as unknown as AsyncIterable<Uint8Array>,
  write: (line) => void process.stdout.write(`${line}\n`),
  log,
});
const host = createHost({ peer, log });

await peer.run();
host.close();
process.exit(0);
