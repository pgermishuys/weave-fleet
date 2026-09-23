import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { HarnessSignInAttempt, HarnessSignIns } from "@/api/client";
import HarnessSignInPanel from "@/components/settings/HarnessSignInPanel.vue";
import { useHarnessSignInStore } from "@/stores/harness-sign-in";

const browser = vi.hoisted(() => ({ remote: false }));
vi.mock("@/lib/harness-sign-in", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/harness-sign-in")>()),
  isRemoteBrowser: () => browser.remote,
}));

// What OpenCode 2.0.9 lists, cut down: a provider signed in twice and through the environment, and two to sign in to.
const signIns: HarnessSignIns = {
  note: "These sign-ins belong to Fleet's separate OpenCode 2 install and are kept in its own database.",
  providers: [
    {
      id: "anthropic",
      name: "Anthropic",
      methods: [{ type: "key", label: "API key", fields: [] }],
      connections: [
        { kind: "credential", id: "cred_work", label: "Work", active: true },
        { kind: "credential", id: "cred_home", label: "Anthropic", active: false },
        { kind: "env", id: "ANTHROPIC_API_KEY", label: "ANTHROPIC_API_KEY", active: false },
      ],
    },
    {
      id: "azure",
      name: "Azure",
      methods: [
        { type: "key", label: "API key", fields: [{ key: "resourceName", type: "string", title: "Enter Azure Resource Name", required: true, hidden: false }] },
        { type: "env", label: "Environment variable", fields: [], environmentVariables: ["AZURE_API_KEY"] },
      ],
      connections: [],
    },
    {
      id: "openai",
      name: "OpenAI",
      methods: [
        { type: "key", label: "API key", fields: [] },
        { type: "oauth", id: "chatgpt-browser", label: "ChatGPT Pro/Plus (browser)", fields: [] },
      ],
      connections: [],
    },
  ],
};

const browserAttempt: HarnessSignInAttempt = {
  id: "con_1",
  url: "https://auth.openai.com/oauth/authorize?redirect_uri=http%3A%2F%2Flocalhost%3A1455%2Fauth%2Fcallback",
  instructions: "Complete authorization in your browser. This window will close automatically.",
  needsCode: false,
  expiresAt: "2026-09-22T12:10:00Z",
  callbackAddress: "http://localhost:1455/auth/callback",
};

let store: ReturnType<typeof useHarnessSignInStore>;

function mountPanel(autoLoad = true) {
  return mount(HarnessSignInPanel, {
    props: { harnessType: "opencode2", harnessName: "OpenCode 2", signInCommand: "opencode2 auth login", autoLoad },
  });
}

beforeEach(() => {
  browser.remote = false;
  store = useHarnessSignInStore();
  vi.spyOn(store, "load").mockImplementation(async () => {
    store.byHarness = { opencode2: signIns };
  });
  vi.spyOn(window, "open").mockReturnValue(null);
});

afterEach(() => {
  vi.useRealTimers();
  vi.restoreAllMocks();
});

async function openSignIn(view: ReturnType<typeof mountPanel>, providerId: string) {
  await view.get(`[data-testid='harness-sign-in-open-${providerId}']`).trigger("click");
}

describe("HarnessSignInPanel", () => {
  it("shows the signed-in providers with the sign-in each uses", async () => {
    const view = mountPanel();
    await flushPromises();

    expect(view.get("[data-testid='harness-sign-in-note']").text()).toContain("separate OpenCode 2 install");
    const work = view.get("[data-testid='harness-sign-in-connection-cred_work']");
    expect(work.text()).toContain("In use");
    expect(work.text()).not.toContain("Use this one");
    expect(view.get("[data-testid='harness-sign-in-connection-cred_home']").text()).toContain("Use this one");
    const env = view.get("[data-testid='harness-sign-in-connection-ANTHROPIC_API_KEY']");
    expect(env.text()).toContain("from Fleet's environment");
    expect(env.text()).not.toContain("Sign out");
  });

  it("waits for a click before asking a harness that isn't turned on", async () => {
    const view = mountPanel(false);
    await flushPromises();
    expect(store.load).not.toHaveBeenCalled();

    await view.get("[data-testid='harness-sign-in-show']").trigger("click");
    await flushPromises();

    expect(store.load).toHaveBeenCalledWith("opencode2");
    expect(view.find("[data-testid='harness-sign-in-provider-azure']").exists()).toBe(true);
  });

  it("finds a provider to sign in to", async () => {
    const view = mountPanel();
    await flushPromises();

    await view.get("[data-testid='harness-sign-in-search']").setValue("azu");

    expect(view.find("[data-testid='harness-sign-in-provider-azure']").exists()).toBe(true);
    expect(view.find("[data-testid='harness-sign-in-provider-openai']").exists()).toBe(false);
  });

  it("signs in with a key and the provider's own fields", async () => {
    const signIn = vi.spyOn(store, "signInWithKey").mockResolvedValue();
    // Listed again, Azure has moved to the signed-in providers, taking its sign-in form away.
    const changed = vi.spyOn(store, "changed").mockImplementation(async () => {
      store.byHarness = {
        opencode2: {
          ...signIns,
          providers: signIns.providers.map((p) => (p.id === "azure"
            ? { ...p, connections: [{ kind: "credential" as const, id: "cred_azure", label: "Azure", active: true }] }
            : p)),
        },
      };
    });
    const view = mountPanel();
    await flushPromises();

    await openSignIn(view, "azure");
    await view.get("[data-testid='sign-in-submit']").trigger("submit");
    expect(view.get("[data-testid='sign-in-error']").text()).toBe("Fill in “Enter Azure Resource Name”.");

    await view.get("[data-testid='sign-in-field-resourceName']").setValue("my-models");
    await view.get("[data-testid='sign-in-key']").setValue("sk-dummy-0000");
    await view.get("form").trigger("submit");
    await flushPromises();

    expect(signIn).toHaveBeenCalledWith("opencode2", "azure", "sk-dummy-0000", { resourceName: "my-models" });
    expect(changed).toHaveBeenCalledWith("opencode2");
    expect(view.get("[data-testid='harness-sign-in-notice']").text()).toBe("Signed in to Azure. New sessions can use its models.");
    expect(view.get("[data-testid='harness-sign-in-connection-cred_azure']").text()).toContain("In use");
    expect(view.find("[data-testid='sign-in-key']").exists()).toBe(false);
  });

  it("shows the harness's reason when it refuses", async () => {
    vi.spyOn(store, "signInWithKey").mockRejectedValue(new Error("Missing required form field: resourceName"));
    const view = mountPanel();
    await flushPromises();

    await openSignIn(view, "openai");
    await view.get("[data-testid='sign-in-key']").setValue("sk-dummy-0000");
    await view.get("form").trigger("submit");
    await flushPromises();

    expect(view.get("[data-testid='sign-in-error']").text()).toBe("Missing required form field: resourceName");
  });

  it("describes the environment method instead of running anything", async () => {
    const view = mountPanel();
    await flushPromises();

    await openSignIn(view, "azure");
    await view.get("[data-testid='sign-in-method-env:']").trigger("click");

    expect(view.get("[data-testid='sign-in-env']").text()).toContain("AZURE_API_KEY");
  });

  it("signs out after asking once more", async () => {
    const signOut = vi.spyOn(store, "signOut").mockResolvedValue();
    const view = mountPanel();
    await flushPromises();

    const button = view.get("[data-testid='harness-sign-out-cred_home']");
    await button.trigger("click");
    expect(signOut).not.toHaveBeenCalled();
    expect(button.text()).toBe("Sign out?");

    await button.trigger("click");
    await flushPromises();

    expect(signOut).toHaveBeenCalledWith("opencode2", "cred_home");
    expect(view.get("[data-testid='harness-sign-in-notice']").text()).toBe("Signed out of Anthropic on Anthropic.");
  });

  it("switches to another sign-in", async () => {
    const use = vi.spyOn(store, "use").mockResolvedValue();
    const view = mountPanel();
    await flushPromises();

    const home = view.get("[data-testid='harness-sign-in-connection-cred_home']");
    await home.findAll("button").find((b) => b.text() === "Use this one")!.trigger("click");
    await flushPromises();

    expect(use).toHaveBeenCalledWith("opencode2", "cred_home");
  });

  it("opens the provider's page and follows the browser sign-in until it's done", async () => {
    vi.useFakeTimers();
    vi.spyOn(store, "start").mockResolvedValue(browserAttempt);
    const status = vi.spyOn(store, "status")
      .mockResolvedValueOnce({ status: "pending" })
      .mockResolvedValueOnce({ status: "complete" });
    const changed = vi.spyOn(store, "changed").mockResolvedValue();
    const view = mountPanel();
    await flushPromises();

    await openSignIn(view, "openai");
    await view.get("[data-testid='sign-in-method-oauth:chatgpt-browser']").trigger("click");
    await view.get("form").trigger("submit");
    await flushPromises();

    expect(window.open).toHaveBeenCalledWith(browserAttempt.url, "_blank", "noopener,noreferrer");
    expect(view.get("[data-testid='sign-in-attempt-url']").attributes("href")).toBe(browserAttempt.url);
    // On this computer the page comes back by itself; pasting its address is only offered.
    expect(view.find("[data-testid='sign-in-remote-note']").exists()).toBe(false);
    expect(view.find("[data-testid='sign-in-paste-open']").exists()).toBe(true);

    await vi.advanceTimersByTimeAsync(2000);
    await vi.advanceTimersByTimeAsync(2000);
    await flushPromises();

    expect(status).toHaveBeenCalledTimes(2);
    expect(changed).toHaveBeenCalledWith("opencode2");
    expect(view.get("[data-testid='harness-sign-in-notice']").text()).toBe("Signed in to OpenAI. New sessions can use its models.");
  });

  it("from another device, says the page won't load and takes its address", async () => {
    browser.remote = true;
    vi.spyOn(store, "start").mockResolvedValue(browserAttempt);
    vi.spyOn(store, "status").mockResolvedValue({ status: "pending" });
    const forward = vi.spyOn(store, "forwardCallback").mockResolvedValue();
    const view = mountPanel();
    await flushPromises();

    await openSignIn(view, "openai");
    await view.get("[data-testid='sign-in-method-oauth:chatgpt-browser']").trigger("click");
    await view.get("form").trigger("submit");
    await flushPromises();

    expect(view.get("[data-testid='sign-in-remote-note']").text()).toContain("http://localhost:1455/auth/callback");
    await view.get("[data-testid='sign-in-landed-on']").setValue("http://localhost:1455/auth/callback?code=abc&state=xyz");
    await view.get("form:has([data-testid='sign-in-landed-on'])").trigger("submit");
    await flushPromises();

    expect(forward).toHaveBeenCalledWith("opencode2", "openai", "con_1", "http://localhost:1455/auth/callback?code=abc&state=xyz");
  });

  it("says why a browser sign-in failed", async () => {
    vi.useFakeTimers();
    vi.spyOn(store, "start").mockResolvedValue(browserAttempt);
    vi.spyOn(store, "status").mockResolvedValue({ status: "failed", message: "access_denied" });
    const view = mountPanel();
    await flushPromises();

    await openSignIn(view, "openai");
    await view.get("[data-testid='sign-in-method-oauth:chatgpt-browser']").trigger("click");
    await view.get("form").trigger("submit");
    await flushPromises();
    await vi.advanceTimersByTimeAsync(2000);
    await flushPromises();

    expect(view.get("[data-testid='sign-in-error']").text()).toBe("The sign-in didn't work: access_denied.");
  });

  it("cancels a browser sign-in left open", async () => {
    vi.spyOn(store, "start").mockResolvedValue(browserAttempt);
    vi.spyOn(store, "status").mockResolvedValue({ status: "pending" });
    const cancel = vi.spyOn(store, "cancel").mockResolvedValue();
    const view = mountPanel();
    await flushPromises();

    await openSignIn(view, "openai");
    await view.get("[data-testid='sign-in-method-oauth:chatgpt-browser']").trigger("click");
    await view.get("form").trigger("submit");
    await flushPromises();
    view.unmount();
    await flushPromises();

    expect(cancel).toHaveBeenCalledWith("opencode2", "openai", "con_1");
  });
});
