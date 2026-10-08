import { describe, expect, it } from "vitest";
import { defineComponent, h, nextTick, shallowRef } from "vue";
import { mount } from "@vue/test-utils";
import MachineScope from "@/components/layout/MachineScope.vue";
import { liveTarget, targetFor, useMachineTarget, type MachineTarget } from "@/lib/machine-target";
import type { MachineConnection } from "@/lib/machines";

const mini: MachineConnection = {
  id: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  name: "mini",
  baseUrl: "http://mini.example.test:2113",
  token: "mini-token-0123456789",
  addedAt: "2026-10-08T00:00:00.000Z",
};

describe("MachineScope", () => {
  it("makes what's inside ask its machine, and rebuilds it when the machine changes", async () => {
    const seen: string[] = [];
    const SessionView = defineComponent({
      setup() {
        seen.push(useMachineTarget().key);
        return () => h("div");
      },
    });
    const target = shallowRef<MachineTarget>(liveTarget());
    const Shell = defineComponent({
      setup() {
        return () => h("div", [
          h(MachineScope, { key: target.value.key, target: target.value }, () => h(SessionView)),
          // Outside the scope (the sidebar, say): the live machine.
          h(SessionView),
        ]);
      },
    });

    mount(Shell);
    expect(seen).toEqual(["home", "home"]);

    target.value = targetFor(mini);
    await nextTick();
    expect(seen).toEqual(["home", "home", mini.id]);

    // The same machine again keeps what's inside as it is.
    target.value = targetFor(mini);
    await nextTick();
    expect(seen).toEqual(["home", "home", mini.id]);
  });
});
