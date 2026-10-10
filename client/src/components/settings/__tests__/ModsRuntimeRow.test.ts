import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import FeaturesSection from "@/components/settings/FeaturesSection.vue";
import type { BunCandidate, ModsRuntimeJob, ModsRuntimeView } from "@/lib/mods-runtime";

const { getMock, putMock } = vi.hoisted(() => ({ getMock: vi.fn(), putMock: vi.fn() }));

vi.mock("@/api/client", () => ({ api: { GET: getMock, PUT: putMock } }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));

const RUNTIME = "/api/features/mods/runtime";

function makeView(overrides: Partial<ModsRuntimeView> = {}): ModsRuntimeView {
  return {
    bun: null,
    configuredPath: null,
    configuredError: null,
    configuredInConfig: false,
    release: {
      version: "1.4.2",
      oldestSafe: "1.4.0",
      note: null,
      size: 35_300_000,
      hasBuild: true,
      installFolder: "~/.weave/runtimes/bun/1.4.2",
      source: "github.com/oven-sh/bun",
    },
    installedSize: 0,
    job: null,
    update: null,
    ...overrides,
  };
}

function installed(version = "1.4.2"): ModsRuntimeView["bun"] {
  return {
    path: `/home/u/.weave/runtimes/bun/${version}/bun`,
    displayPath: `~/.weave/runtimes/bun/${version}/bun`,
    source: "installed",
    version,
    safe: true,
    message: null,
  };
}

function job(overrides: Partial<ModsRuntimeJob> = {}): ModsRuntimeJob {
  return {
    phase: "downloading",
    kind: "install",
    version: "1.4.2",
    message: null,
    reason: null,
    bytesReceived: 12_000_000,
    bytesTotal: 35_300_000,
    startedAt: new Date().toISOString(),
    from: null,
    ...overrides,
  };
}

const usable: BunCandidate = {
  path: "/home/u/.bun/bin/bun",
  displayPath: "~/.bun/bin/bun",
  version: "1.4.5",
  status: "usable",
  message: null,
};

interface Server {
  view: ModsRuntimeView;
  candidates: BunCandidate[];
  calls: { method: string; path: string; body?: unknown }[];
  /** An answer for one route, instead of the view. */
  overrides: Record<string, () => Response>;
}

let server: Server;

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function installFetch(): void {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = new URL(String(input), "http://localhost");
      const method = (init?.method ?? "GET").toUpperCase();
      const body = typeof init?.body === "string" ? (JSON.parse(init.body) as unknown) : undefined;
      server.calls.push({ method, path: url.pathname, body });
      const key = `${method} ${url.pathname}`;
      if (server.overrides[key]) return server.overrides[key]!();
      if (url.pathname === "/api/features/mods") return json({ on: false });
      if (key === `GET ${RUNTIME}`) return json(server.view);
      if (key === `GET ${RUNTIME}/found`) return json({ candidates: server.candidates });
      if (url.pathname.startsWith(RUNTIME)) return json(server.view);
      return new Response("{}", { status: 404 });
    }),
  );
}

async function mountRow(view: ModsRuntimeView, preference?: "true" | "false"): Promise<VueWrapper> {
  server.view = view;
  getMock.mockResolvedValue({ data: preference ? { Mods: preference } : {} });
  const wrapper = mount(FeaturesSection, { attachTo: document.body });
  await flushPromises();
  return wrapper;
}

function panel(wrapper: VueWrapper) {
  return wrapper.find("[data-testid='mods-panel']");
}

function stateOf(wrapper: VueWrapper): string | undefined {
  return panel(wrapper).attributes("data-state");
}

function button(wrapper: VueWrapper, label: string) {
  const found = wrapper.findAll("button").find((item) => item.text() === label);
  if (!found) throw new Error(`No "${label}" button. Buttons: ${wrapper.findAll("button").map((item) => item.text()).join(" | ")}`);
  return found;
}

function hasButton(wrapper: VueWrapper, label: string): boolean {
  return wrapper.findAll("button").some((item) => item.text() === label);
}

const toggle = (wrapper: VueWrapper) => wrapper.get("[data-testid='mods-switch']");
const called = (method: string, path: string) => server.calls.filter((call) => call.method === method && call.path === path);

describe("Mods row: turning Mods on", () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
    getMock.mockReset();
    putMock.mockReset();
    putMock.mockResolvedValue({});
    server = { view: makeView(), candidates: [], calls: [], overrides: {} };
    installFetch();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    document.body.innerHTML = "";
  });

  it("1 off: shows nothing under the description, and doesn't look for Bun on render", async () => {
    const wrapper = await mountRow(makeView());

    expect(toggle(wrapper).attributes("aria-checked")).toBe("false");
    expect(panel(wrapper).exists()).toBe(false);
    expect(called("GET", `${RUNTIME}/found`)).toHaveLength(0);
  });

  it("2 confirm: switching on with no Bun says what Fleet will download, and the switch stays off", async () => {
    const wrapper = await mountRow(makeView());

    await toggle(wrapper).trigger("click");
    await flushPromises();

    expect(called("GET", `${RUNTIME}/found`)).toHaveLength(1);
    expect(stateOf(wrapper)).toBe("confirm");
    const region = wrapper.get("[aria-labelledby]");
    expect(wrapper.get(`#${region.attributes("aria-labelledby")}`).text()).toBe("Mods need Bun");
    expect(panel(wrapper).text()).toContain("Fleet downloads Bun 1.4.2 (about 35 MB) from github.com/oven-sh/bun");
    expect(panel(wrapper).text()).toContain("checks it against a checksum built into Fleet");
    expect(panel(wrapper).text()).toContain("installs it in ~/.weave/runtimes/bun/1.4.2. Nothing else on your computer changes.");
    expect(toggle(wrapper).attributes("aria-checked")).toBe("false");
    expect(toggle(wrapper).classes()).toContain("ring-2");
    expect(putMock).not.toHaveBeenCalled();
    const buttons = panel(wrapper).findAll("button").map((item) => item.text());
    expect(buttons).toEqual(["Turn on and install", "Cancel"]);
  });

  it("2 confirm: Cancel folds the row back to step 1", async () => {
    const wrapper = await mountRow(makeView());
    await toggle(wrapper).trigger("click");
    await flushPromises();

    await button(wrapper, "Cancel").trigger("click");

    expect(panel(wrapper).exists()).toBe(false);
    expect(toggle(wrapper).attributes("aria-checked")).toBe("false");
    expect(called("POST", `${RUNTIME}/install`)).toHaveLength(0);
  });

  it("2 confirm: Turn on and install posts the install, then the switch shows on", async () => {
    const wrapper = await mountRow(makeView());
    await toggle(wrapper).trigger("click");
    await flushPromises();
    server.overrides[`POST ${RUNTIME}/install`] = () => {
      server.view = makeView({ job: job() });
      getMock.mockResolvedValue({ data: { Mods: "true" } });
      return json(server.view, 202);
    };

    await button(wrapper, "Turn on and install").trigger("click");
    await flushPromises();

    expect(called("POST", `${RUNTIME}/install`)).toHaveLength(1);
    expect(toggle(wrapper).attributes("aria-checked")).toBe("true");
    expect(stateOf(wrapper)).toBe("progress");
    expect(putMock).not.toHaveBeenCalled();
  });

  it("2c found: offers the Bun it found first, in three buttons", async () => {
    server.candidates = [usable];
    const wrapper = await mountRow(makeView());

    await toggle(wrapper).trigger("click");
    await flushPromises();

    expect(stateOf(wrapper)).toBe("found");
    const text = panel(wrapper).text();
    expect(text).toContain("Fleet found Bun 1.4.5 at ~/.bun/bin/bun.");
    expect(text).toContain("bun upgrade");
    expect(text).toContain("Or Fleet can download its own Bun 1.4.2 (about 35 MB) from github.com/oven-sh/bun");
    expect(panel(wrapper).findAll("button").map((item) => item.text())).toEqual(["Use my Bun 1.4.5", "Download Fleet's own", "Cancel"]);
  });

  it("2c found: ignores candidates that can't be used", async () => {
    server.candidates = [{ ...usable, status: "too-old", version: "1.2.0" }];
    const wrapper = await mountRow(makeView());

    await toggle(wrapper).trigger("click");
    await flushPromises();

    expect(stateOf(wrapper)).toBe("confirm");
  });

  it("2c found: Use my Bun saves its path, which turns the switch on", async () => {
    server.candidates = [usable];
    const wrapper = await mountRow(makeView());
    await toggle(wrapper).trigger("click");
    await flushPromises();
    server.overrides[`PUT ${RUNTIME}/bun-path`] = () => {
      server.view = makeView({
        bun: { path: usable.path, displayPath: "~/.bun/bin/bun", source: "configured", version: "1.4.5", safe: true },
        configuredPath: usable.path,
      });
      getMock.mockResolvedValue({ data: { Mods: "true" } });
      return json(server.view);
    };

    await button(wrapper, "Use my Bun 1.4.5").trigger("click");
    await flushPromises();

    expect(called("PUT", `${RUNTIME}/bun-path`)[0]?.body).toEqual({ path: usable.path });
    expect(toggle(wrapper).attributes("aria-checked")).toBe("true");
    expect(stateOf(wrapper)).toBe("ready");
  });

  it("2c found: Download Fleet's own posts the install", async () => {
    server.candidates = [usable];
    const wrapper = await mountRow(makeView());
    await toggle(wrapper).trigger("click");
    await flushPromises();

    await button(wrapper, "Download Fleet's own").trigger("click");
    await flushPromises();

    expect(called("POST", `${RUNTIME}/install`)).toHaveLength(1);
  });

  it("2 confirm: with no build for this computer, says so and offers Use my own Bun…", async () => {
    const view = makeView();
    view.release = { ...view.release, hasBuild: false, size: null };
    const wrapper = await mountRow(view);

    await toggle(wrapper).trigger("click");
    await flushPromises();

    expect(panel(wrapper).text()).toContain("Fleet has no Bun build for this computer. Install Bun yourself and point Fleet at it.");
    expect(hasButton(wrapper, "Turn on and install")).toBe(false);
    expect(hasButton(wrapper, "Use my own Bun…")).toBe(true);
  });

  it("6 switches straight on when Fleet's own Bun is already installed", async () => {
    const wrapper = await mountRow(makeView({ bun: installed(), installedSize: 80_000_000 }));
    expect(stateOf(wrapper)).toBe("off-again");

    await toggle(wrapper).trigger("click");
    await flushPromises();

    expect(putMock).toHaveBeenLastCalledWith("/api/preferences/{key}", { params: { path: { key: "Mods" } }, body: { value: "true" } });
    expect(called("GET", `${RUNTIME}/found`)).toHaveLength(0);
    expect(toggle(wrapper).attributes("aria-checked")).toBe("true");
    expect(stateOf(wrapper)).toBe("ready");
  });

  it("6 switches straight on when a Bun path is set", async () => {
    const wrapper = await mountRow(makeView({ configuredPath: "/opt/tools/bun/bin/bun", configuredError: "It isn't working." }));

    await toggle(wrapper).trigger("click");
    await flushPromises();

    expect(putMock).toHaveBeenCalled();
    expect(stateOf(wrapper)).not.toBe("confirm");
  });
});

describe("Mods row: installing", () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
    getMock.mockReset();
    putMock.mockReset();
    putMock.mockResolvedValue({});
    server = { view: makeView(), candidates: [], calls: [], overrides: {} };
    installFetch();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    document.body.innerHTML = "";
  });

  it("3a downloading: names the version, the megabytes, a progress bar, the phases and Cancel", async () => {
    const wrapper = await mountRow(makeView({ job: job() }), "true");

    expect(stateOf(wrapper)).toBe("progress");
    const text = panel(wrapper).text();
    expect(text).toContain("Downloading Bun 1.4.2");
    expect(text).toContain("12 of 35 MB");
    expect(text).toContain("This keeps going if you leave Settings. Mods start as soon as it's done.");
    const bar = panel(wrapper).get("[role='progressbar']");
    expect(bar.attributes("aria-valuenow")).toBe("12");
    expect(bar.attributes("aria-valuemax")).toBe("35");
    expect(bar.attributes("aria-valuetext")).toBe("12 of 35 MB");
    const steps = panel(wrapper).findAll("[data-testid^='mods-step-']");
    expect(steps.map((step) => [step.text(), step.attributes("data-status")])).toEqual([
      ["Download", "current"],
      ["Check", "todo"],
      ["Unpack", "todo"],
    ]);
    expect(hasButton(wrapper, "Cancel")).toBe(true);
  });

  it("3b checking: Download is done, Check is current", async () => {
    const wrapper = await mountRow(makeView({ job: job({ phase: "verifying", bytesReceived: 35_300_000 }) }), "true");

    expect(panel(wrapper).text()).toContain("Checking the download");
    expect(panel(wrapper).text()).toContain("35 of 35 MB");
    const steps = panel(wrapper).findAll("[data-testid^='mods-step-']");
    expect(steps.map((step) => step.attributes("data-status"))).toEqual(["done", "current", "todo"]);
  });

  it("3c unpacking: Check is done, Unpack is current", async () => {
    const wrapper = await mountRow(makeView({ job: job({ phase: "extracting", bytesReceived: 35_300_000 }) }), "true");

    expect(panel(wrapper).text()).toContain("Unpacking Bun 1.4.2");
    const steps = panel(wrapper).findAll("[data-testid^='mods-step-']");
    expect(steps.map((step) => step.attributes("data-status"))).toEqual(["done", "done", "current"]);
  });

  it("3d back: an install that started before the row opened says it kept going", async () => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date("2026-10-10T09:00:40Z"));
    const wrapper = await mountRow(makeView({ job: job({ bytesReceived: 29_000_000, startedAt: "2026-10-10T09:00:00Z" }) }), "true");

    expect(panel(wrapper).text()).toContain("Started 40 seconds ago. It kept going while you were away.");
    expect(panel(wrapper).text()).toContain("29 of 35 MB");
  });

  it("Cancel during an install posts cancel", async () => {
    const wrapper = await mountRow(makeView({ job: job() }), "true");

    await button(wrapper, "Cancel").trigger("click");
    await flushPromises();

    expect(called("POST", `${RUNTIME}/cancel`)).toHaveLength(1);
  });

  it("announces the phase politely, not every megabyte", async () => {
    const wrapper = await mountRow(makeView({ job: job() }), "true");

    const live = panel(wrapper).get("[aria-live='polite']");
    expect(live.text()).toBe("Downloading Bun 1.4.2");
  });

  it("4 ready: one quiet line with where Bun lives", async () => {
    const wrapper = await mountRow(makeView({ bun: installed(), installedSize: 80_000_000 }), "true");

    expect(stateOf(wrapper)).toBe("ready");
    expect(panel(wrapper).text()).toContain("Mods on · Bun 1.4.2");
    expect(panel(wrapper).text()).toContain("~/.weave/runtimes/bun/1.4.2/bun");
  });
});

describe("Mods row: failed", () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
    getMock.mockReset();
    putMock.mockReset();
    putMock.mockResolvedValue({});
    server = { view: makeView(), candidates: [], calls: [], overrides: {} };
    installFetch();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    document.body.innerHTML = "";
  });

  const offline = job({
    phase: "failed",
    reason: "offline",
    message: "Fleet couldn't reach github.com: the connection timed out. Check that this computer is online, then try again.",
  });

  it("5a offline: red heading, the reason, the blocked-GitHub hint, Retry and Use my own Bun…", async () => {
    const wrapper = await mountRow(makeView({ job: offline }), "false");

    expect(stateOf(wrapper)).toBe("failed");
    expect(toggle(wrapper).attributes("aria-checked")).toBe("false");
    const text = panel(wrapper).text();
    expect(text).toContain("Couldn't download Bun");
    expect(text).toContain("Fleet couldn't reach github.com: the connection timed out. Check that this computer is online, then try again.");
    expect(text).toContain("If GitHub is blocked here, install Bun yourself and point Fleet at it.");
    expect(panel(wrapper).findAll("button").map((item) => item.text())).toEqual(["Retry", "Use my own Bun…"]);
    expect(panel(wrapper).get("h4").classes()).toContain("text-error");
  });

  it("5b proxy: names the host to allow", async () => {
    const wrapper = await mountRow(
      makeView({
        job: job({
          phase: "failed",
          reason: "blocked",
          message: "github.com answered 403 Forbidden, so a proxy or firewall is probably blocking downloads from GitHub.",
        }),
      }),
      "false",
    );

    const text = panel(wrapper).text();
    expect(text).toContain("github.com answered 403 Forbidden, so a proxy or firewall is probably blocking downloads from GitHub.");
    expect(text).toContain("Ask whoever runs your network to allow github.com/oven-sh/bun, or install Bun yourself and point Fleet at it.");
  });

  it("shows the other reasons as the server words them", async () => {
    const wrapper = await mountRow(
      makeView({ job: job({ phase: "failed", reason: "stopped", message: "The download stopped after 21 of 35 MB. Try again." }) }),
      "false",
    );

    expect(panel(wrapper).text()).toContain("The download stopped after 21 of 35 MB. Try again.");
    expect(hasButton(wrapper, "Retry")).toBe(true);
  });

  it("Retry posts the install again", async () => {
    const wrapper = await mountRow(makeView({ job: offline }), "false");

    await button(wrapper, "Retry").trigger("click");
    await flushPromises();

    expect(called("POST", `${RUNTIME}/install`)).toHaveLength(1);
  });

  it("no build: Retry is hidden", async () => {
    const wrapper = await mountRow(
      makeView({
        job: job({
          phase: "failed",
          reason: "no-build",
          message: "Fleet has no Bun build for this computer. Install Bun yourself and point Fleet at it.",
        }),
      }),
      "false",
    );

    expect(panel(wrapper).text()).toContain("Fleet has no Bun build for this computer.");
    expect(hasButton(wrapper, "Retry")).toBe(false);
    expect(hasButton(wrapper, "Use my own Bun…")).toBe(true);
  });

  it("a cancelled install folds back to the off row, with no error", async () => {
    const wrapper = await mountRow(makeView({ job: job({ phase: "failed", reason: "cancelled", message: "The install was cancelled." }) }), "false");

    expect(panel(wrapper).exists()).toBe(false);
  });

  describe("5c own Bun", () => {
    async function openForm() {
      const wrapper = await mountRow(makeView({ job: offline }), "false");
      await button(wrapper, "Use my own Bun…").trigger("click");
      await flushPromises();
      return wrapper;
    }

    it("shows a labelled path field with its hint, and Use this Bun / Back", async () => {
      const wrapper = await openForm();

      expect(stateOf(wrapper)).toBe("own-bun");
      const input = panel(wrapper).get("input");
      expect(panel(wrapper).get(`label[for='${input.attributes("id")}']`).exists()).toBe(true);
      expect(input.attributes("aria-describedby")).toContain("hint");
      expect(panel(wrapper).text()).toContain("Install Bun 1.4 or later yourself, then give Fleet the full path to it.");
      expect(panel(wrapper).text()).toContain("The path must start at the root, like /opt/tools/bun/bin/bun or C:\\Tools\\bun\\bun.exe.");
      expect(panel(wrapper).findAll("button").map((item) => item.text())).toEqual(["Use this Bun", "Back"]);
    });

    it("refuses a relative path before asking the server", async () => {
      const wrapper = await openForm();

      await panel(wrapper).get("input").setValue("bun/bin/bun");
      await button(wrapper, "Use this Bun").trigger("click");
      await flushPromises();

      expect(called("PUT", `${RUNTIME}/bun-path`)).toHaveLength(0);
      const alert = panel(wrapper).get("[role='alert']");
      expect(alert.text()).toContain("The path must start at the root");
      expect(panel(wrapper).get("input").attributes("aria-invalid")).toBe("true");
      expect(panel(wrapper).get("input").attributes("aria-describedby")).toContain(alert.attributes("id"));
    });

    it("shows the server's 400 under the field", async () => {
      const wrapper = await openForm();
      server.overrides[`PUT ${RUNTIME}/bun-path`] = () => json({ error: "That Bun is older than 1.4." }, 400);

      await panel(wrapper).get("input").setValue("/opt/old/bun");
      await button(wrapper, "Use this Bun").trigger("click");
      await flushPromises();

      expect(called("PUT", `${RUNTIME}/bun-path`)[0]?.body).toEqual({ path: "/opt/old/bun" });
      expect(panel(wrapper).get("[role='alert']").text()).toContain("That Bun is older than 1.4.");
      expect(stateOf(wrapper)).toBe("own-bun");
    });

    it("accepts an absolute path, saves it and goes to the ready line", async () => {
      const wrapper = await openForm();
      server.overrides[`PUT ${RUNTIME}/bun-path`] = () => {
        server.view = makeView({
          bun: { path: "/opt/tools/bun/bin/bun", displayPath: "/opt/tools/bun/bin/bun", source: "configured", version: "1.4.5", safe: true },
          configuredPath: "/opt/tools/bun/bin/bun",
        });
        getMock.mockResolvedValue({ data: { Mods: "true" } });
        return json(server.view);
      };

      await panel(wrapper).get("input").setValue("/opt/tools/bun/bin/bun");
      await button(wrapper, "Use this Bun").trigger("click");
      await flushPromises();

      expect(stateOf(wrapper)).toBe("ready");
      expect(toggle(wrapper).attributes("aria-checked")).toBe("true");
    });

    it("Back returns to the failure", async () => {
      const wrapper = await openForm();

      await button(wrapper, "Back").trigger("click");

      expect(stateOf(wrapper)).toBe("failed");
    });
  });
});

describe("Mods row: on, off and updates", () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
    getMock.mockReset();
    putMock.mockReset();
    putMock.mockResolvedValue({});
    server = { view: makeView(), candidates: [], calls: [], overrides: {} };
    installFetch();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    document.body.innerHTML = "";
  });

  const own = (path: string, displayPath: string, version = "1.4.5", safe = true): ModsRuntimeView["bun"] => ({
    path,
    displayPath,
    source: "configured",
    version,
    safe,
    message: null,
  });

  it("6 your own Bun: the line names it and its version", async () => {
    const wrapper = await mountRow(makeView({ bun: own("/opt/tools/bun/bin/bun", "/opt/tools/bun/bin/bun"), configuredPath: "/opt/tools/bun/bin/bun" }), "true");

    expect(stateOf(wrapper)).toBe("ready");
    expect(panel(wrapper).text()).toContain("Mods on · your Bun 1.4.5");
    expect(panel(wrapper).text()).toContain("/opt/tools/bun/bin/bun");
  });

  it("6b the Bun Fleet found: the line shortens the home folder", async () => {
    const wrapper = await mountRow(makeView({ bun: own("/home/u/.bun/bin/bun", "~/.bun/bin/bun"), configuredPath: "/home/u/.bun/bin/bun" }), "true");

    expect(panel(wrapper).text()).toContain("Mods on · your Bun 1.4.5");
    expect(panel(wrapper).text()).toContain("~/.bun/bin/bun");
  });

  it("7 off again: says Bun stays installed, and writes the preference false", async () => {
    const wrapper = await mountRow(makeView({ bun: installed(), installedSize: 80_000_000 }), "true");

    await toggle(wrapper).trigger("click");
    await flushPromises();

    expect(putMock).toHaveBeenLastCalledWith("/api/preferences/{key}", { params: { path: { key: "Mods" } }, body: { value: "false" } });
    expect(stateOf(wrapper)).toBe("off-again");
    expect(panel(wrapper).text()).toContain("Mods are off. Bun stays installed in ~/.weave/runtimes/bun/1.4.2 (about 80 MB), so turning mods on again is instant.");
  });

  it("7 has no line when nothing is installed", async () => {
    const wrapper = await mountRow(makeView({ installedSize: 0 }), "false");

    expect(panel(wrapper).exists()).toBe(false);
  });

  it("8 updating: names the new version and says mods keep running", async () => {
    const wrapper = await mountRow(
      makeView({
        bun: installed("1.4.2"),
        job: job({ kind: "update", version: "1.4.3", from: "1.4.2", bytesReceived: 14_000_000 }),
      }),
      "true",
    );

    expect(stateOf(wrapper)).toBe("progress");
    const text = panel(wrapper).text();
    expect(text).toContain("Updating to Bun 1.4.3");
    expect(text).toContain("14 of 35 MB");
    expect(text).toContain("Mods keep running on Bun 1.4.2 until it's done, then move over. Fleet looks for a new Bun when it starts and every 4 hours.");
    expect(hasButton(wrapper, "Cancel")).toBe(false);
  });

  it("8b updated: back to the one quiet line, on the new version", async () => {
    const wrapper = await mountRow(
      makeView({ bun: installed("1.4.3"), job: job({ kind: "update", version: "1.4.3", phase: "succeeded", from: "1.4.2" }) }),
      "true",
    );

    expect(stateOf(wrapper)).toBe("ready");
    expect(panel(wrapper).text()).toContain("Mods on · Bun 1.4.3");
    expect(panel(wrapper).text()).toContain("~/.weave/runtimes/bun/1.4.3/bun");
  });

  it("9 security: amber heading, with the reason Fleet is installing now", async () => {
    const wrapper = await mountRow(
      makeView({
        bun: installed("1.4.2"),
        update: { version: "1.4.3", security: true },
        job: job({ kind: "security", version: "1.4.3", from: "1.4.2", bytesReceived: 9_000_000 }),
      }),
      "true",
    );

    expect(stateOf(wrapper)).toBe("progress");
    expect(panel(wrapper).get("h4").text()).toBe("Security fix: updating to Bun 1.4.3");
    expect(panel(wrapper).get("h4").classes()).toContain("text-idle");
    expect(panel(wrapper).get("[role='progressbar']").classes().join(" ")).toContain("idle");
    expect(panel(wrapper).text()).toContain("Bun 1.4.2 has a security problem that 1.4.3 fixes, so Fleet is installing it now. Mods keep running meanwhile.");
    expect(panel(wrapper).text()).toContain("9 of 35 MB");
  });

  it("9b security failed: amber, the cause, mods keep running, Retry posts the install", async () => {
    const wrapper = await mountRow(
      makeView({
        bun: installed("1.4.2"),
        update: { version: "1.4.3", security: true },
        job: job({
          kind: "security",
          version: "1.4.3",
          from: "1.4.2",
          phase: "failed",
          reason: "offline",
          message: "Fleet couldn't reach github.com: the connection timed out. Check that this computer is online, then try again.",
        }),
      }),
      "true",
    );

    expect(stateOf(wrapper)).toBe("security-failed");
    expect(panel(wrapper).get("h4").text()).toBe("Bun 1.4.2 needs a security fix");
    expect(panel(wrapper).get("h4").classes()).toContain("text-idle");
    const text = panel(wrapper).text();
    expect(text).toContain("Bun 1.4.3 fixes a security problem in 1.4.2. Fleet couldn't download it: the connection timed out.");
    expect(text).toContain("Mods keep running on 1.4.2, and Fleet tries again every 4 hours.");
    expect(toggle(wrapper).attributes("aria-checked")).toBe("true");

    await button(wrapper, "Retry").trigger("click");
    await flushPromises();
    expect(called("POST", `${RUNTIME}/install`)).toHaveLength(1);
  });

  describe("10 your own Bun is old", () => {
    const old = () => {
      const view = makeView({
        bun: own("/opt/tools/bun/bin/bun", "/opt/tools/bun/bin/bun", "1.4.2", false),
        configuredPath: "/opt/tools/bun/bin/bun",
      });
      view.release = { ...view.release, version: "1.4.3" };
      return view;
    };

    it("says to run bun upgrade, with Copy and Use Fleet's own Bun instead", async () => {
      const wrapper = await mountRow(old(), "true");

      expect(stateOf(wrapper)).toBe("own-old");
      expect(panel(wrapper).get("h4").text()).toBe("Your Bun 1.4.2 needs a security fix");
      expect(panel(wrapper).get("h4").classes()).toContain("text-idle");
      expect(panel(wrapper).text()).toContain("Bun 1.4.3 fixes a security problem in 1.4.2. Update your Bun and Fleet picks up the new version by itself. Mods keep running meanwhile.");
      expect(panel(wrapper).get("code").text()).toBe("bun upgrade");
      expect(panel(wrapper).findAll("button").map((item) => item.text())).toEqual(["Copy", "Use Fleet's own Bun instead"]);
    });

    it("Copy puts bun upgrade on the clipboard", async () => {
      const writeText = vi.fn().mockResolvedValue(undefined);
      vi.stubGlobal("navigator", { ...navigator, clipboard: { writeText } });
      const wrapper = await mountRow(old(), "true");

      await button(wrapper, "Copy").trigger("click");
      await flushPromises();

      expect(writeText).toHaveBeenCalledWith("bun upgrade");
      expect(hasButton(wrapper, "Copied")).toBe(true);
    });

    it("Use Fleet's own Bun instead clears the path, then installs", async () => {
      const wrapper = await mountRow(old(), "true");

      await button(wrapper, "Use Fleet's own Bun instead").trigger("click");
      await flushPromises();

      const order = server.calls.filter((call) => call.path.startsWith(RUNTIME) && call.method !== "GET").map((call) => `${call.method} ${call.path}`);
      expect(order).toEqual([`PUT ${RUNTIME}/bun-path`, `POST ${RUNTIME}/install`]);
      expect(called("PUT", `${RUNTIME}/bun-path`)[0]?.body).toEqual({ path: null });
    });

    it("hides Use Fleet's own Bun when configuration sets the path", async () => {
      const view = old();
      view.configuredInConfig = true;
      const wrapper = await mountRow(view, "true");

      expect(hasButton(wrapper, "Use Fleet's own Bun instead")).toBe(false);
    });
  });
});
