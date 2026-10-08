import { beforeEach, describe, expect, it, vi } from "vitest";
import type { SessionListItem } from "@/api/client";
import type { MachineTarget } from "@/lib/machine-target";

const pinSession = vi.fn<(machine: MachineTarget, sessionId: string, beforeSessionId: string | null) => Promise<number>>();
const unpinSession = vi.fn<(machine: MachineTarget, sessionId: string) => Promise<void>>();
/** The session list is the live machine's, so pins go there. */
const live = expect.objectContaining({ key: "home", isLive: true });
const get = vi.fn();

vi.mock("@/composables/use-session-actions", () => ({ pinSession, unpinSession }));
vi.mock("@/api/client", () => ({ api: { GET: get } }));

const { useSessionPinsStore } = await import("@/stores/session-pins");
const { useSessionsStore } = await import("@/stores/sessions");

function session(id: string, pinOrder: number | null = null): SessionListItem {
  return { instanceId: `instance-${id}`, session: { id, title: id, time: { created: 1, updated: 2 }, tags: [] }, pinOrder } as unknown as SessionListItem;
}

function orderOf(id: string): number | null | undefined {
  return useSessionsStore().sessions.find((item) => item.session.id === id)?.pinOrder;
}

describe("session pins", () => {
  beforeEach(() => {
    pinSession.mockReset();
    unpinSession.mockReset().mockResolvedValue();
    get.mockReset();
    useSessionsStore().setSessions([session("a", 1), session("b", 2), session("c")]);
  });

  it("pins at once where it will go, tells the server, and keeps the server's answer", async () => {
    let answer!: (order: number) => void;
    pinSession.mockReturnValue(new Promise((resolve) => (answer = resolve)));
    const pins = useSessionPinsStore();

    const pinning = pins.pin("c", "b");
    expect(orderOf("c")).toBe(1.5);
    expect(pinSession).toHaveBeenCalledWith(live, "c", "b");

    answer(1.5);
    await pinning;
    expect(orderOf("c")).toBe(1.5);
    expect(get).not.toHaveBeenCalled();
  });

  it("puts it back and says so when the server refuses", async () => {
    pinSession.mockRejectedValue(new Error("Archived sessions can't be pinned; restore it first."));
    const pins = useSessionPinsStore();

    await pins.pin("c");

    expect(orderOf("c")).toBeNull();
    expect(pins.error).toBe("Archived sessions can't be pinned; restore it first.");
  });

  it("reads every pin's order again when the server numbered them again", async () => {
    pinSession.mockResolvedValue(2);
    get.mockResolvedValue({ data: [session("a", 1), session("c", 2), session("b", 3)] });
    const pins = useSessionPinsStore();

    await pins.pin("c", "b");

    expect([orderOf("a"), orderOf("c"), orderOf("b")]).toEqual([1, 2, 3]);
  });

  it("unpins at once, and puts the pin back when the server refuses", async () => {
    const pins = useSessionPinsStore();

    await pins.unpin("a");
    expect(unpinSession).toHaveBeenCalledWith(live, "a");
    expect(orderOf("a")).toBeNull();

    unpinSession.mockRejectedValue(new Error("Session not found"));
    await pins.unpin("b");
    expect(orderOf("b")).toBe(2);
    expect(pins.error).toBe("Session not found");
  });

  it("moves a pin up or down past its neighbour, and not past the ends", async () => {
    pinSession.mockImplementation(async () => 0.5);
    const pins = useSessionPinsStore();

    await pins.move("a", -1);
    expect(pinSession).not.toHaveBeenCalled();

    await pins.move("b", -1);
    expect(pinSession).toHaveBeenCalledWith(live, "b", "a");
    expect(orderOf("b")).toBe(0.5);
  });
});
