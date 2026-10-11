import { describe, expect, test } from "bun:test";
import * as mod from "../../../samples/http-check/mod";
import { checked, fixture, pills, render } from "./render";

describe("the http-check sample", () => {
  test("passes the static check and hooks the row line of shell tools", async () => {
    const report = await checked("http-check");
    expect(report.hooks).toEqual([{ event: "ui.render", matcher: { component: "ToolUse", props: { tool: ["bash", "shell"] } } }]);
  });

  test("curl -i, -sI and -iL against a local server (real output)", () => {
    expect(mod.parse(fixture("curl-i-200.txt"))).toEqual({ code: 200, reason: "OK", bytes: 946, hops: 0 });
    expect(mod.parse(fixture("curl-sI-301.txt"))).toEqual({ code: 301, reason: "Moved Permanently", bytes: 0, hops: 0 });
    expect(mod.parse(fixture("curl-i-404.txt"))).toMatchObject({ code: 404, reason: "Not Found", hops: 0 });
    expect(mod.parse(fixture("curl-sI-503.txt"))).toMatchObject({ code: 503, bytes: 22 });
    expect(mod.parse(fixture("curl-iL-redirect.txt"))).toEqual({ code: 200, reason: "OK", bytes: 946, hops: 1 });
  });

  test("curl's progress meter ahead of the headers, as an agent's shell captured it (real output)", () => {
    expect(mod.parse(fixture("curl-i-200-progress-meter.txt"))).toEqual({ code: 200, reason: "OK", bytes: 946, hops: 0 });
  });

  test("HTTP/2 has no reason, and a body without headers is not a response", () => {
    expect(mod.parse("HTTP/2 204 \r\ndate: x\r\n\r\n")).toMatchObject({ code: 204, reason: "", bytes: undefined });
    expect(mod.parse('{"berths":[]}')).toBeUndefined();
  });

  test("sizes", () => {
    expect([512, 2048, 5 * 1024 * 1024].map(mod.size)).toEqual(["512 B", "2.0 KB", "5.0 MB"]);
  });

  test("the row line: tone by class of status", async () => {
    const tone = async (file: string) => {
      const tree: any = await render(mod, "ToolUse", { input: { command: "curl -i localhost:5471" }, output: fixture(file) });
      return [tree.children[0].tone, ...pills(tree)];
    };
    expect(await tone("curl-i-200.txt")).toEqual(["good", "200 OK", "946 B"]);
    expect(await tone("curl-sI-301.txt")).toEqual(["warn", "301 Moved Permanently", "0 B"]);
    expect(await tone("curl-i-404.txt")).toEqual(["bad", "404 Not Found", "9 B"]);
    expect(await tone("curl-iL-redirect.txt")).toEqual(["good", "200 OK", "1 redirect", "946 B"]);
    expect(await render(mod, "ToolUse", { input: { command: "ls" }, output: fixture("curl-i-200.txt") })).toBe("next");
  });
});
