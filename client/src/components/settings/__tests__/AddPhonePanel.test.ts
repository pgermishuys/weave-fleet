import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import AddPhonePanel from "@/components/settings/AddPhonePanel.vue";
import { encodePairingPayload } from "@/lib/pairing";

const fetchOnMachine = vi.hoisted(() => vi.fn());
vi.mock("@/lib/machines", () => ({ fetchOnMachine }));

const urls = ["http://studio.tail4f2a.ts.net:6262", "http://192.168.1.20:6262"];
const addresses = [
  { url: urls[0], kind: "tailnet" },
  { url: urls[1], kind: "lan" },
];

function codeResponse(carried: string[] = urls) {
  const payload = { v: 1 as const, machineId: "m1", machineName: "studio", url: carried[0], secret: "s3cret", urls: carried };
  return new Response(JSON.stringify({
    secret: "s3cret",
    manualCode: "H4KD-92QX",
    expiresAt: new Date(Date.now() + 600_000).toISOString(),
    url: `${carried[0]}/pair#p=${encodePairingPayload(payload)}`,
    payload,
  }), { status: 200 });
}

function mountPanel(props: { publicUrl?: string | null; addresses?: typeof addresses } = {}) {
  return mount(AddPhonePanel, {
    props: { machineName: "studio", publicUrl: props.publicUrl ?? null, addresses: props.addresses ?? addresses, deviceNames: [] },
  });
}

beforeEach(() => {
  // The panel also asks whether this is the desktop app; a plain Fleet answers 404.
  fetchOnMachine.mockImplementation(async (_machine: unknown, path: string) => path === "/api/machine/reach" ? new Response("{}", { status: 404 }) : codeResponse());
});

afterEach(() => vi.clearAllMocks());

describe("AddPhonePanel: no address to choose", () => {
  it("shows where the phone will reach the machine before there is a code, and no address field", () => {
    const wrapper = mountPanel();

    expect(wrapper.get("[data-testid='add-phone-readiness']").text()).toContain("Your phone will reach studio");
    expect(wrapper.get("[data-testid='add-phone-reach-home']").text()).toContain("192.168.1.20");
    expect(wrapper.get("[data-testid='add-phone-reach-tailscale']").text()).toContain("studio.tail4f2a.ts.net");
    expect(wrapper.find("[data-testid='add-phone-url']").exists()).toBe(false);
    expect(wrapper.text()).not.toContain("Which address will the phone open?");
  });

  it("asks for a code without naming an address, and shows what the code carries", async () => {
    const wrapper = mountPanel();

    await wrapper.get("[data-testid='add-phone-start']").trigger("click");
    await flushPromises();

    const call = fetchOnMachine.mock.calls.find(([, path]) => path === "/api/machine/pairing");
    expect(JSON.parse((call?.[2] as RequestInit).body as string)).toEqual({});
    expect(wrapper.find("[data-testid='add-phone-qr']").exists()).toBe(true);
    expect(wrapper.get("[data-testid='add-phone-code']").text()).toBe("H4KD-92QX");
    expect(wrapper.get("[data-testid='add-phone-readiness']").text()).toContain("At home, on your Wi-Fi");
    expect(wrapper.get("[data-testid='add-phone-readiness']").text()).toContain("Away from home, with Tailscale on the phone");
  });

  it("keeps the address field under Use my own address, and sends that address first", async () => {
    const wrapper = mountPanel();

    await wrapper.get("[data-testid='add-phone-set-address']").trigger("click");
    await wrapper.get("[data-testid='add-phone-url']").setValue("https://fleet.example.com");
    fetchOnMachine.mockImplementation(async (_machine: unknown, path: string) => path === "/api/machine/reach" ? new Response("{}", { status: 404 }) : path === "/api/machine" ? new Response("{}", { status: 200 }) : codeResponse(["https://fleet.example.com", ...urls]));
    await wrapper.get("form").trigger("submit");
    await flushPromises();

    const pairing = fetchOnMachine.mock.calls.find(([, path]) => path === "/api/machine/pairing");
    expect(JSON.parse((pairing?.[2] as RequestInit).body as string)).toEqual({ baseUrl: "https://fleet.example.com" });
    expect(wrapper.get("[data-testid='add-phone-reach-own']").text()).toContain("fleet.example.com");
  });

  it("opens the address field when Fleet only listens on this machine", async () => {
    fetchOnMachine.mockImplementation(async (_machine: unknown, path: string) => path === "/api/machine/reach"
      ? new Response("{}", { status: 404 })
      : new Response(JSON.stringify({ error: "Fleet only listens on this machine, so no phone can reach it." }), { status: 400 }));
    const wrapper = mountPanel({ addresses: [] });

    await wrapper.get("[data-testid='add-phone-start']").trigger("click");
    await flushPromises();

    expect(wrapper.find("[data-testid='add-phone-url']").exists()).toBe(true);
    expect(wrapper.text()).toContain("only listens on this machine");
  });
});
