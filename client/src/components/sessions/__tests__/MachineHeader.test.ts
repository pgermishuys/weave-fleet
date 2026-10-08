import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, describe, expect, it } from "vitest";
import MachineHeader from "@/components/sessions/MachineHeader.vue";

function mountHeader(props: Record<string, unknown>) {
  return mount(MachineHeader, {
    attachTo: document.body,
    props: { name: "hangar", expanded: true, ...props },
    // The menu teleports to the body.
    global: { stubs: { teleport: false } },
  });
}

async function openMenu(view: ReturnType<typeof mountHeader>): Promise<void> {
  await view.get("[data-testid='machine-header']").trigger("contextmenu", { clientX: 40, clientY: 40 });
  await flushPromises();
}

function menuItem(testId: string): HTMLElement | null {
  return document.body.querySelector<HTMLElement>(`[data-testid='${testId}']`);
}

describe("MachineHeader menu", () => {
  afterEach(() => {
    document.body.innerHTML = "";
  });

  it("offers a new session there and Work here on a machine that isn't live", async () => {
    const view = mountHeader({ menu: true });
    await openMenu(view);

    expect(menuItem("machine-menu-new-session")?.textContent).toMatch(/New session on\s*hangar/);
    expect(menuItem("machine-menu-work-here")?.textContent?.trim()).toBe("Work here");

    menuItem("machine-menu-new-session")!.click();
    await flushPromises();
    expect(view.emitted("newSession")).toHaveLength(1);
    expect(view.emitted("toggle")).toBeUndefined();

    await openMenu(view);
    menuItem("machine-menu-work-here")!.click();
    await flushPromises();
    expect(view.emitted("workHere")).toHaveLength(1);
    view.unmount();
  });

  it("offers only New session on the live machine", async () => {
    const view = mountHeader({ menu: true, live: true });
    await openMenu(view);

    expect(menuItem("machine-menu-new-session")?.textContent?.trim()).toBe("New session");
    expect(menuItem("machine-menu-work-here")).toBeNull();
    view.unmount();
  });

  it("can't start a session on a machine it can't reach, but can still work there", async () => {
    const view = mountHeader({ menu: true, unreachable: true });
    await openMenu(view);

    expect(menuItem("machine-menu-new-session")?.hasAttribute("data-disabled")).toBe(true);
    expect(menuItem("machine-menu-work-here")?.hasAttribute("data-disabled")).toBe(false);
    view.unmount();
  });

  it("has no menu unless asked for one", async () => {
    const view = mountHeader({});
    await openMenu(view);

    expect(menuItem("machine-menu")).toBeNull();
    view.unmount();
  });
});
