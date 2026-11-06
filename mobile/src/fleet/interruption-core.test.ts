import { describe, expect, test } from "bun:test";
import type { SessionListItem } from "@fleet/api/client";
import type { DomainEvent } from "@fleet/lib/domain-events";
import { interruptedEntries, interruptionBanner, toInterruption } from "./interruption-core";
import { applySessionsEvent } from "./session-list";

const cut = { at: "2026-11-06T10:00:00Z", waiting: false, lastCall: "bash: bun test", changedFiles: 3, auto: false };
const item = (id: string, interrupted?: unknown, over: object = {}) => ({ session: { id, title: id }, interrupted, ...over }) as unknown as SessionListItem;
const event = (type: string, payload: object) => ({ type, payload }) as unknown as DomainEvent;

describe("toInterruption", () => {
  test("reads the wire shape, with auto false unless true", () => {
    expect(toInterruption(cut)).toEqual(cut);
    expect(toInterruption({ at: cut.at })).toEqual({ at: cut.at, waiting: false, lastCall: null, changedFiles: null, auto: false });
    expect(toInterruption({ ...cut, auto: true })?.auto).toBe(true);
  });
  test("null and junk are no interruption", () => {
    expect(toInterruption(null)).toBeNull();
    expect(toInterruption({ at: "nope" })).toBeNull();
  });
});

describe("session.interrupted", () => {
  test("sets and clears the mark on the row", () => {
    const set = applySessionsEvent([item("a")], event("session.interrupted", { sessionId: "a", interruption: cut }));
    expect(set.list[0].interrupted).toEqual(cut);
    expect(set.refetch).toBe(false);
    expect(applySessionsEvent(set.list, event("session.interrupted", { sessionId: "a", interruption: null })).list[0].interrupted).toBeNull();
  });
  test("an unknown session asks for a refetch", () => {
    expect(applySessionsEvent([], event("session.interrupted", { sessionId: "z", interruption: cut })).refetch).toBe(true);
  });
});

describe("interruptedEntries and the banner", () => {
  const lists = [{ machineId: "m", machineName: "Laptop", items: [item("a", cut), item("b"), item("c", cut, { archivedAt: "2026-11-06T09:00:00Z" }), item("d", { ...cut, auto: true })] }];
  test("lists the cut-off sessions that are not archived", () => {
    expect(interruptedEntries(lists).map((e) => e.item.session.id)).toEqual(["a", "d"]);
  });
  test("banner words", () => {
    expect(interruptionBanner([])).toBeNull();
    expect(interruptionBanner(interruptedEntries(lists).slice(0, 1))).toEqual({ text: "Fleet restarted. 1 session was cut off.", automatic: false });
    expect(interruptionBanner(interruptedEntries([{ ...lists[0], items: [item("a", cut), item("b", cut)] }]))).toEqual({ text: "Fleet restarted. 2 sessions were cut off.", automatic: false });
    expect(interruptionBanner(interruptedEntries(lists))).toEqual({ text: "Fleet restarted. Continuing 1 session.", automatic: true });
  });
});
