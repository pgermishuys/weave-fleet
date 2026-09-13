/**
 * A pretend dev server for the browser canvas in mock mode. It listens on its own port, so the canvas frames
 * another origin as it does through Fleet's preview gateway, and its pages load the real preview bridge
 * (`src/WeaveFleet.Api/Browser/preview-bridge.js`). A websocket with Vite's `vite-hmr` protocol stands in for hot
 * reload: `send({ type: "update", css })` swaps the page's theme without a reload, `send({ type: "full-reload" })`
 * reloads it. Used by vite-plugin-mock-api.ts only.
 */

import { createServer, type IncomingMessage } from "http";
import { createHash } from "crypto";
import { readFileSync } from "fs";
import type { AddressInfo, Socket } from "net";

export interface MockPreview {
  /** Where the canvas loads the preview, e.g. `http://127.0.0.1:41234`. */
  origin: string;
  /** Sends a message to every open page's hot-reload socket. */
  send(message: Record<string, unknown>): void;
  close(): void;
}

const WEBSOCKET_GUID = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
const BRIDGE_PATH = "/__fleet_browser/nav.js";
const HMR_PATH = "/__mock_hmr";

const PAGES: Record<string, { title: string; body: string }> = {
  "/": {
    title: "Storefront",
    body: `
      <section class="hero">
        <h1>Autumn collection</h1>
        <p>Warm layers, made to last. Free returns within 30 days.</p>
        <a class="button" href="/cart">View cart</a>
      </section>
      <section class="grid">
        <article class="card"><div class="swatch" style="background:#d6b89a"></div><h2>Wool overshirt</h2><p>R 1 450</p></article>
        <article class="card"><div class="swatch" style="background:#8a9a7b"></div><h2>Field jacket</h2><p>R 2 300</p></article>
        <article class="card"><div class="swatch" style="background:#5b6b82"></div><h2>Merino scarf</h2><p>R 540</p></article>
      </section>`,
  },
  "/cart": {
    title: "Cart · Storefront",
    body: `
      <section class="hero">
        <h1>Your cart</h1>
        <p>1 item · R 1 450</p>
        <a class="button" href="/">Keep shopping</a>
      </section>`,
  },
};

const STYLE = `
  * { box-sizing: border-box; }
  body { margin: 0; font-family: system-ui, sans-serif; color: #1c1917; background: #fafaf9; }
  header { display: flex; align-items: center; gap: 20px; padding: 14px 24px; border-bottom: 1px solid #e7e5e4; background: #fff; }
  header strong { color: var(--brand); font-size: 17px; }
  header nav { display: flex; gap: 14px; font-size: 14px; }
  header a { color: #57534e; text-decoration: none; }
  .hero { padding: 36px 24px 20px; }
  .hero h1 { margin: 0 0 8px; font-size: 28px; }
  .hero p { margin: 0 0 18px; color: #57534e; }
  .button { display: inline-block; padding: 9px 16px; border-radius: 8px; background: var(--brand); color: #fff; text-decoration: none; font-size: 14px; }
  .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(160px, 1fr)); gap: 16px; padding: 8px 24px 32px; }
  .card { padding: 12px; border: 1px solid #e7e5e4; border-radius: 10px; background: #fff; }
  .card h2 { margin: 10px 0 4px; font-size: 15px; }
  .card p { margin: 0; color: var(--brand); font-weight: 600; }
  .swatch { height: 90px; border-radius: 6px; }
`;

function page(path: string, themeCss: string): string {
  const { title, body } = PAGES[path] ?? PAGES["/"];
  return `<!doctype html>
<html>
<head>
<meta charset="utf-8">
<script src="${BRIDGE_PATH}"></script>
<title>${title}</title>
<style id="theme">${themeCss}</style>
<style>${STYLE}</style>
</head>
<body>
<header><strong>Storefront</strong><nav><a href="/">Shop</a><a href="/cart">Cart</a></nav></header>
<main>${body}</main>
<script>
  (function () {
    var socket = new WebSocket((location.protocol === "https:" ? "wss://" : "ws://") + location.host + "${HMR_PATH}", "vite-hmr");
    socket.onmessage = function (event) {
      var message = JSON.parse(event.data);
      if (message.type === "update" && message.css) document.getElementById("theme").textContent = message.css;
      if (message.type === "full-reload") location.reload();
    };
  })();
</script>
</body>
</html>`;
}

/** A text frame from the server: unmasked, final, and short enough for a 16-bit length. */
function textFrame(text: string): Buffer {
  const payload = Buffer.from(text, "utf8");
  const header = payload.length < 126
    ? Buffer.from([0x81, payload.length])
    : Buffer.from([0x81, 126, payload.length >> 8, payload.length & 0xff]);
  return Buffer.concat([header, payload]);
}

export function startMockPreview(bridgeScriptPath: string): Promise<MockPreview> {
  const sockets = new Set<Socket>();
  let themeCss = ":root { --brand: #c2410c; }";

  const server = createServer((req, res) => {
    const path = new URL(req.url ?? "/", "http://mock").pathname;
    if (path === BRIDGE_PATH) {
      let script: string;
      try {
        script = readFileSync(bridgeScriptPath, "utf-8");
      } catch {
        script = `console.warn("[mock-preview] ${bridgeScriptPath} not found: no preview bridge");`;
      }
      res.writeHead(200, { "Content-Type": "text/javascript; charset=utf-8", "Cache-Control": "no-store" });
      res.end(script);
      return;
    }

    res.writeHead(200, { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" });
    res.end(page(path, themeCss));
  });

  server.on("upgrade", (req: IncomingMessage, socket: Socket) => {
    const key = req.headers["sec-websocket-key"];
    if (req.url !== HMR_PATH || typeof key !== "string") {
      socket.destroy();
      return;
    }

    const accept = createHash("sha1").update(key + WEBSOCKET_GUID).digest("base64");
    const protocols = String(req.headers["sec-websocket-protocol"] ?? "").split(",").map((value) => value.trim());
    socket.write(
      "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
        + `Sec-WebSocket-Accept: ${accept}\r\n`
        + (protocols.includes("vite-hmr") ? "Sec-WebSocket-Protocol: vite-hmr\r\n" : "")
        + "\r\n",
    );
    sockets.add(socket);
    socket.on("data", (data: Buffer) => {
      // The page closing its socket (opcode 8); everything else it sends is ignored.
      if ((data[0] & 0x0f) === 0x8) socket.end();
    });
    socket.on("close", () => sockets.delete(socket));
    socket.on("error", () => sockets.delete(socket));
  });

  return new Promise((resolvePreview, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const { port } = server.address() as AddressInfo;
      resolvePreview({
        origin: `http://127.0.0.1:${port}`,
        send(message) {
          if (message.type === "update" && typeof message.css === "string") themeCss = message.css;
          const frame = textFrame(JSON.stringify(message));
          for (const socket of sockets) socket.write(frame);
        },
        close() {
          for (const socket of sockets) socket.destroy();
          server.close();
        },
      });
    });
  });
}
