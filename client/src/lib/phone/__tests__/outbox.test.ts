import { beforeEach, describe, expect, it, vi } from "vitest";
import { editHeld, flushHeld, heldFor, hold, removeHeld } from "../outbox";

describe("outbox", () => {
  beforeEach(() => localStorage.clear());

  it("holds messages per machine and session, in order", () => {
    hold("hangar", "s1", "first", 1);
    hold("hangar", "s1", "second", 2);
    hold("falcon", "s1", "elsewhere", 3);

    expect(heldFor("hangar", "s1").map((m) => m.text)).toEqual(["first", "second"]);
    expect(heldFor("falcon", "s1").map((m) => m.text)).toEqual(["elsewhere"]);
  });

  it("sends in order when the machine is back", async () => {
    hold("hangar", "s1", "first", 1);
    hold("hangar", "s1", "second", 2);
    const sent: string[] = [];

    const left = await flushHeld("hangar", "s1", async (text) => {
      sent.push(text);
      return null;
    });

    expect(sent).toEqual(["first", "second"]);
    expect(left).toEqual([]);
  });

  it("stops at the first failure and keeps it, and the rest, with the reason", async () => {
    hold("hangar", "s1", "first", 1);
    hold("hangar", "s1", "second", 2);
    const send = vi.fn(async () => "Couldn't reach hangar.");

    const left = await flushHeld("hangar", "s1", send);

    expect(send).toHaveBeenCalledTimes(1);
    expect(left.map((m) => [m.text, m.error])).toEqual([["first", "Couldn't reach hangar."], ["second", undefined]]);
  });

  it("counts a thrown error as a failure, never a drop", async () => {
    hold("hangar", "s1", "first", 1);

    const left = await flushHeld("hangar", "s1", async () => {
      throw new Error("offline");
    });

    expect(left.map((m) => m.error)).toEqual(["offline"]);
  });

  it("edits and removes held messages", () => {
    const item = hold("hangar", "s1", "typo", 1);
    editHeld("hangar", "s1", item.id, "fixed");
    expect(heldFor("hangar", "s1").map((m) => m.text)).toEqual(["fixed"]);

    removeHeld("hangar", "s1", item.id);
    expect(heldFor("hangar", "s1")).toEqual([]);
  });
});
