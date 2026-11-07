import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { ref } from "vue";
import { defineMachineStore } from "@/lib/machine-store";
import type { MachineTarget } from "@/lib/machine-target";

const { live } = vi.hoisted(() => ({ live: { key: "home", connection: null, isLive: true, api: { name: "live" } } }));
vi.mock("@/lib/machine-target", () => ({ liveTarget: () => live }));

const useThing = defineMachineStore("thing", (target) => {
  const value = ref(0);
  return { value, askedKey: target.key, askedApi: target.api };
});

const falcon = { key: "falcon", connection: null, isLive: false, api: { name: "falcon" } } as unknown as MachineTarget;
const kestrel = { key: "kestrel", connection: null, isLive: false, api: { name: "kestrel" } } as unknown as MachineTarget;

describe("defineMachineStore", () => {
  beforeEach(() => setActivePinia(createPinia()));

  it("is the live machine's store when no machine is given, and the same one every time", () => {
    const store = useThing();
    store.value = 3;

    expect(useThing().value).toBe(3);
    expect(store.askedKey).toBe("home");
    expect(store.$id).toBe("thing");
  });

  it("gives each machine its own instance with its own state, asking that machine", () => {
    useThing().value = 1;
    useThing(falcon).value = 2;
    useThing(kestrel).value = 3;

    expect(useThing().value).toBe(1);
    expect(useThing(falcon).value).toBe(2);
    expect(useThing(kestrel).value).toBe(3);
    expect(useThing(falcon).askedApi).toStrictEqual({ name: "falcon" });
    expect(useThing(kestrel).askedApi).toStrictEqual({ name: "kestrel" });
  });
});
