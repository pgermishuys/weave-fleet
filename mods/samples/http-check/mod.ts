import type { Register } from "fleet-mods";

// Draws a `curl -i` / `curl -I` response as its status (green for 2xx, amber for 3xx, red for 4xx and 5xx) and, when the
// server sent Content-Length, its size.
export type Response = { code: number; reason: string; bytes?: number; hops: number };

/** Reads the last response in curl's output (the one after any redirects). Undefined when there are no headers. */
export function parse(raw: string): Response | undefined {
  const lines = raw.split(/\r?\n/).map((l) => l.replace(/^[<>*]\s?/, ""));
  const starts = lines.flatMap((l, i) => (/^HTTP\/[\d.]+ \d{3}/.test(l) ? [i] : []));
  if (starts.length === 0) return undefined;
  const last = lines.slice(starts[starts.length - 1]);
  const [, code, reason] = /^HTTP\/[\d.]+ (\d{3}) ?(.*)$/.exec(last[0]) ?? [];
  const blank = last.findIndex((l) => l.trim() === "");
  const length = /^content-length:\s*(\d+)/im.exec(last.slice(0, blank < 0 ? undefined : blank).join("\n"));
  return { code: Number(code), reason: reason.trim(), bytes: length ? Number(length[1]) : undefined, hops: starts.length - 1 };
}

export const size = (bytes: number): string =>
  bytes < 1024 ? `${bytes} B` : bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`;

export const register: Register = (on) => {
  on("ui.render", { component: "ToolUse", props: { tool: ["bash", "shell"] } }, async ($, e, next) => {
    if (e.component !== "ToolUse" || !/\bcurl\b/.test(String(e.props.input.command ?? ""))) return next(e);
    if (e.props.status !== "completed" && e.props.status !== "error") return next(e); // not while it runs
    const response = parse(e.props.output);
    if (!response) return next(e);

    const { Box, Pill } = $.ui.resolve(e);
    const tone = response.code < 300 ? "good" : response.code < 400 ? "warn" : "bad";
    return Box({
      flexDirection: "row",
      gap: 1,
      children: [
        Pill({ tone, label: `${response.code} ${response.reason}`.trim() }),
        response.hops > 0 && Pill({ tone: "neutral", label: `${response.hops} redirect${response.hops === 1 ? "" : "s"}` }),
        response.bytes !== undefined && Pill({ tone: "neutral", label: size(response.bytes) }),
      ],
    });
  });
};
