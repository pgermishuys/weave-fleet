import { afterEach, describe, expect, it } from "vitest";
import {
  forgetPageStates,
  keepPageState,
  keptPageState,
  MAX_REPLY_LENGTH,
  MAX_STATE_LENGTH,
  pageStateMessage,
  MAX_PAGE_HEIGHT,
  readPageMessage,
  readPageSize,
} from "@/lib/page-bridge";

describe("readPageMessage", () => {
  it("reads the three messages a page can send", () => {
    expect(readPageMessage({ type: "fleet:page-hello" })).toEqual({ type: "fleet:page-hello" });
    expect(readPageMessage({ type: "fleet:page-reply", text: "# Re: Plan" })).toEqual({ type: "fleet:page-reply", text: "# Re: Plan" });
    expect(readPageMessage({ type: "fleet:page-state", state: { answers: { a: 1 } } }))
      .toEqual({ type: "fleet:page-state", state: { answers: { a: 1 } } });
  });

  it("ignores anything else", () => {
    for (const data of [null, "fleet:page-hello", 42, {}, { type: "hello", fleet: 1 }, { type: "fleet:page-send", text: "x" }]) {
      expect(readPageMessage(data)).toBeNull();
    }
  });

  it("needs a reply with text, within the limit", () => {
    expect(readPageMessage({ type: "fleet:page-reply" })).toBeNull();
    expect(readPageMessage({ type: "fleet:page-reply", text: 7 })).toBeNull();
    expect(readPageMessage({ type: "fleet:page-reply", text: "  \n " })).toBeNull();
    expect(readPageMessage({ type: "fleet:page-reply", text: "x".repeat(MAX_REPLY_LENGTH + 1) })).toBeNull();
  });

  it("needs state that is JSON, within the limit", () => {
    const cyclic: Record<string, unknown> = {};
    cyclic.self = cyclic;
    expect(readPageMessage({ type: "fleet:page-state", state: cyclic })).toBeNull();
    expect(readPageMessage({ type: "fleet:page-state", state: () => 1 })).toBeNull();
    expect(readPageMessage({ type: "fleet:page-state", state: "x".repeat(MAX_STATE_LENGTH) })).toBeNull();
    expect(readPageMessage({ type: "fleet:page-state", state: null })).toEqual({ type: "fleet:page-state", state: null });
  });
});

describe("kept page state", () => {
  afterEach(() => forgetPageStates());

  it("gives back the last state a page saved, and null for a page with none", () => {
    keepPageState("pg_a", { answers: { one: "1" } });
    keepPageState("pg_a", { answers: { one: "2" } });

    expect(keptPageState("pg_a")).toEqual({ answers: { one: "2" } });
    expect(pageStateMessage("pg_b")).toEqual({ type: "fleet:page-state", state: null });
  });

  it("drops the pages saved longest ago once 50 are kept", () => {
    for (let i = 0; i < 51; i++) keepPageState(`pg_${i}`, i);

    expect(keptPageState("pg_0")).toBeNull();
    expect(keptPageState("pg_1")).toBe(1);
    expect(keptPageState("pg_50")).toBe(50);
  });
});

describe("readPageSize", () => {
  const size = (params: unknown) => ({ jsonrpc: "2.0", method: "ui/notifications/size-changed", params });

  it("reads the height a page reports, rounded up", () => {
    expect(readPageSize(size({ height: 312.4 }))).toBe(313);
    expect(readPageSize(size({ height: 0 }))).toBe(0);
  });

  it("caps a page that reports more than the tallest frame", () => {
    expect(readPageSize(size({ height: 99_999 }))).toBe(MAX_PAGE_HEIGHT);
  });

  it("ignores anything else", () => {
    expect(readPageSize({ type: "fleet:page-hello" })).toBeNull();
    expect(readPageSize({ method: "ui/notifications/size-changed", params: { height: 10 } })).toBeNull();
    expect(readPageSize(size({ height: "10" }))).toBeNull();
    expect(readPageSize(size({ height: -1 }))).toBeNull();
    expect(readPageSize(size({ height: Number.NaN }))).toBeNull();
    expect(readPageSize(size(null))).toBeNull();
    expect(readPageSize(null)).toBeNull();
  });
});
