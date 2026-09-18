import { DOMWrapper, flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { computed, ref, shallowRef, watchEffect, type Ref } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { NO_PROFILE } from "@/api/client";
import type { BranchInfo, HarnessCatalog, HarnessInfo, HarnessProfile, RepositoryDetail, ScannedRepository, WorktreeInfo } from "@/api/client";
import NewSessionComposer from "@/components/sessions/NewSessionComposer.vue";
import { toAgentOptions } from "@/composables/use-agents";
import { toModelOptions } from "@/composables/use-models";
import { NEW_SESSION_DEFAULTS_KEY } from "@/composables/use-new-session-defaults";
import { clearSentPrompts, useSentPrompts } from "@/composables/use-send-prompt";
import { createGitHubSessionSourcePreset } from "@/lib/github-session-source";
import { useHarnessProfilesStore } from "@/stores/harness-profiles";
import { useSessionsStore } from "@/stores/sessions";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useAppShellStore } from "@/stores/app-shell";
import { useHarnessSetupStore } from "@/stores/harness-setup";

const mocks = vi.hoisted(() => ({
  navigate: vi.fn(),
  createSession: vi.fn(),
  refreshRepositories: vi.fn(),
  inspectFolder: vi.fn(),
  addFolderToFleet: vi.fn(),
  search: { value: { projectId: undefined as string | undefined, source: undefined as string | undefined } },
}));

const repositories = ref<ScannedRepository[]>([]);
const worktrees = ref<WorktreeInfo[]>([]);
const harnesses = ref<HarnessInfo[]>([]);
const noHarnessReason = shallowRef<string | null>(null);
const createError = shallowRef<string | undefined>(undefined);
const isCreating = shallowRef(false);

vi.mock("@tanstack/vue-router", () => ({
  useNavigate: () => mocks.navigate,
  useSearch: () => mocks.search,
}));

vi.mock("@/composables/use-repositories", () => ({
  useRepositories: () => ({
    repositories,
    isLoading: shallowRef(false),
    error: shallowRef(null),
    scannedAt: shallowRef(1),
    refresh: mocks.refreshRepositories,
  }),
}));

vi.mock("@/lib/folder-access", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/folder-access")>()),
  inspectFolder: mocks.inspectFolder,
  addFolderToFleet: mocks.addFolderToFleet,
}));

vi.mock("@/composables/use-projects", () => ({
  useProjects: () => ({
    projects: ref([
      { id: "scratch", name: "Scratch", type: "scratch" },
      { id: "project-fleet", name: "Fleet Core", type: "user" },
    ]),
    isLoading: shallowRef(false),
    isRefreshing: shallowRef(false),
    error: shallowRef(undefined),
    refetch: vi.fn(),
  }),
}));

vi.mock("@/composables/use-worktrees", () => ({
  useWorktrees: () => ({ worktrees, isLoading: shallowRef(false), error: shallowRef(null) }),
}));

function branch(name: string, extra: Partial<BranchInfo> = {}): BranchInfo {
  return {
    name,
    shortHash: "abc1234",
    message: `tip of ${name}`,
    author: "",
    authorEmail: "",
    date: "",
    isCurrent: false,
    isRemote: name.startsWith("origin/"),
    ...extra,
  };
}

const repositoryDetail = shallowRef<RepositoryDetail | null>(null);

vi.mock("@/composables/use-repository-detail", () => ({
  useRepositoryDetail: () => ({
    detail: repositoryDetail,
    isLoading: shallowRef(false),
    error: shallowRef(null),
  }),
}));

vi.mock("@/composables/use-enabled-harnesses", () => ({
  useEnabledHarnesses: () => ({
    enabledHarnesses: computed(() => harnesses.value),
    defaultHarnessType: computed(() => "opencode"),
    noHarnessReason: computed(() => noHarnessReason.value),
  }),
}));

const catalog = shallowRef<HarnessCatalog | null>(null);
const catalogRequests: { harnessType: string; directory: string | null; profile?: string }[] = [];

vi.mock("@/composables/use-harness-catalog", () => ({
  useHarnessCatalog: (harnessType: Ref<string>, directory: Ref<string | null>, profile?: Ref<string | undefined>) => {
    watchEffect(() => {
      if (harnessType.value) {
        catalogRequests.push({
          harnessType: harnessType.value,
          directory: directory.value,
          ...(profile?.value ? { profile: profile.value } : {}),
        });
      }
    });
    return {
      catalog,
      agents: computed(() => toAgentOptions(catalog.value?.agents ?? [])),
      models: computed(() => toModelOptions(catalog.value?.providers ?? [])),
      isSupported: computed(() => catalog.value?.supported === true),
      isCurrent: computed(() => catalog.value !== null),
      isLoading: shallowRef(false),
      error: shallowRef(null),
    };
  },
}));

vi.mock("@/composables/use-session-actions", () => ({
  useCreateSession: () => ({
    createSession: mocks.createSession,
    isLoading: isCreating,
    error: createError,
  }),
}));

const rocket: ScannedRepository = { name: "rocket", path: "/home/me/src/rocket", parentRoot: "/home/me/src" };
const comet: ScannedRepository = { name: "comet", path: "/home/me/src/comet", parentRoot: "/home/me/src" };

const opencode = { type: "opencode", displayName: "OpenCode", available: true, userEnabled: true } as HarnessInfo;
const pi = { type: "pi", displayName: "Pi", available: true, userEnabled: true } as HarnessInfo;

function rememberFolder(folder: object, workspaceByRepository: Record<string, string> = {}): void {
  localStorage.setItem(NEW_SESSION_DEFAULTS_KEY, JSON.stringify({
    lastFolder: folder,
    recentFolders: [folder],
    workspaceByRepository,
  }));
}

let wrapper: VueWrapper | null = null;

async function mountComposer(): Promise<VueWrapper> {
  // Menus are portaled to <body>, so let them teleport for real (the global setup stubs it).
  wrapper = mount(NewSessionComposer, { attachTo: document.body, global: { stubs: { teleport: false } } });
  await flushPromises();
  return wrapper;
}

/** Where portaled menus end up. */
function inDocument(): DOMWrapper<Element> {
  return new DOMWrapper(document.body);
}

function folderOption(label: string) {
  const option = inDocument().findAll("[role='option']").find((candidate) => candidate.text().includes(label));
  if (!option) {
    throw new Error(`No folder option "${label}"`);
  }
  return option;
}

function textarea(view: VueWrapper) {
  return view.get<HTMLTextAreaElement>("[data-testid='new-session-message']");
}

async function type(view: VueWrapper, text: string): Promise<void> {
  await textarea(view).setValue(text);
}

async function pressEnter(view: VueWrapper, options: { shiftKey?: boolean } = {}): Promise<void> {
  await textarea(view).trigger("keydown", { key: "Enter", ...options });
  await flushPromises();
}

async function openFolderMenu(view: VueWrapper): Promise<void> {
  await view.get("[data-testid='new-session-folder-chip']").trigger("click");
  await flushPromises();
}

function lastCreateCall(): [string | undefined, Record<string, unknown>] {
  return mocks.createSession.mock.calls.at(-1) as [string | undefined, Record<string, unknown>];
}

beforeEach(() => {
  localStorage.clear();
  // Sent prompts are kept per session id outside Pinia, and every test creates session-1.
  clearSentPrompts("session-1");
  mocks.navigate.mockReset().mockResolvedValue(undefined);
  mocks.createSession.mockReset().mockResolvedValue({
    instanceId: "instance-1",
    workspaceId: "workspace-1",
    session: { id: "session-1", title: "New", time: { created: 0, updated: 0 }, tags: [] },
  });
  mocks.search.value = { projectId: undefined, source: undefined };
  mocks.refreshRepositories.mockReset().mockResolvedValue(undefined);
  mocks.addFolderToFleet.mockReset().mockResolvedValue(undefined);
  mocks.inspectFolder.mockReset().mockImplementation(async (path: string) => ({
    path,
    exists: true,
    isGitRepo: false,
    isWithinRoots: true,
  }));
  repositories.value = [rocket, comet];
  worktrees.value = [];
  harnesses.value = [opencode];
  noHarnessReason.value = null;
  createError.value = undefined;
  isCreating.value = false;
  catalog.value = null;
  catalogRequests.length = 0;
  repositoryDetail.value = {
    name: "rocket",
    path: "/home/me/src/rocket",
    branch: "feature/x",
    uncommittedCount: 0,
    totalCommitCount: 0,
    firstCommitDate: null,
    lastCommitDate: null,
    branches: [
      branch("feature/x", { isCurrent: true }),
      branch("main"),
      branch("origin/main"),
      branch("origin/release/2.0"),
    ],
    tags: [],
    recentCommits: [],
    remotes: [],
    readmeContent: null,
    readmeFilename: null,
    defaultBranch: "main",
    defaultBase: "origin/main",
  };
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = null;
});

describe("NewSessionComposer", () => {
  it("puts the cursor in the message box", async () => {
    const view = await mountComposer();

    expect(document.activeElement).toBe(textarea(view).element);
  });

  describe("chips", () => {
    it("shows the workspace chip for a repository", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();

      expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("rocket");
      expect(view.get("[data-testid='new-session-workspace-chip']").text()).toContain("New worktree");
    });

    it("remembers the workspace per repository", async () => {
      rememberFolder({ kind: "repository", path: rocket.path }, { [rocket.path]: "current" });
      const view = await mountComposer();

      expect(view.get("[data-testid='new-session-workspace-chip']").text()).toContain("Current checkout · feature/x");
      expect(view.get("[data-testid='new-session-plan']").text())
        .toContain("Works directly in ~/src/rocket on feature/x.");
    });

    it("hides the workspace chip for a plain folder and for no folder", async () => {
      rememberFolder({ kind: "directory", path: "/tmp/notes" });
      const view = await mountComposer();

      expect(view.find("[data-testid='new-session-workspace-chip']").exists()).toBe(false);

      await openFolderMenu(view);
      await folderOption("No folder").trigger("click");

      expect(view.find("[data-testid='new-session-workspace-chip']").exists()).toBe(false);
    });

    it("shows the harness picker only when more than one harness is enabled", async () => {
      const single = await mountComposer();
      expect(single.find("[data-testid='new-session-harness']").exists()).toBe(false);
      single.unmount();

      harnesses.value = [opencode, pi];
      const several = await mountComposer();
      expect(several.find("[data-testid='new-session-harness']").exists()).toBe(true);
    });

    describe("profile", () => {
      const withProfiles = { ...opencode, capabilities: { supportsProfiles: true } } as HarnessInfo;

      function profile(id: string, name: string, isDefault = false): HarnessProfile {
        return {
          id, harnessType: "opencode", name, content: `{ "model": "${id}/model" }`, isDefault,
          openSessions: 0, createdAt: "", updatedAt: "",
        };
      }

      function seedProfiles(profiles: HarnessProfile[]): void {
        const store = useHarnessProfilesStore();
        store.byHarness = { opencode: profiles };
        vi.spyOn(store, "load").mockResolvedValue();
      }

      it("has no chip while there are no profiles, and sends none", async () => {
        harnesses.value = [withProfiles];
        seedProfiles([]);
        rememberFolder({ kind: "directory", path: "/home/me/notes" });
        const view = await mountComposer();

        expect(view.find("[data-testid='new-session-profile']").exists()).toBe(false);
        await type(view, "Hello");
        await pressEnter(view);
        expect(lastCreateCall()[1].harnessProfileId).toBeUndefined();
      });

      it("has no chip for a harness without profiles", async () => {
        seedProfiles([profile("work", "Work", true)]);
        const view = await mountComposer();

        expect(view.find("[data-testid='new-session-profile']").exists()).toBe(false);
      });

      it("starts on the default profile and sends it", async () => {
        harnesses.value = [withProfiles];
        seedProfiles([profile("work", "Work", true), profile("local", "Local")]);
        rememberFolder({ kind: "directory", path: "/home/me/notes" });
        const view = await mountComposer();

        expect(view.get("[data-testid='new-session-profile']").text()).toContain("Work");
        await type(view, "Hello");
        await pressEnter(view);
        expect(lastCreateCall()[1].harnessProfileId).toBe("work");
      });

      it("sends the profile picked, and none when that's the pick", async () => {
        harnesses.value = [withProfiles];
        seedProfiles([profile("work", "Work", true), profile("local", "Local")]);
        rememberFolder({ kind: "directory", path: "/home/me/notes" });
        const view = await mountComposer();

        await view.get("[data-testid='new-session-profile']").trigger("click");
        await flushPromises();
        await inDocument().get("[data-testid='new-session-profile-none']").trigger("click");
        await flushPromises();

        expect(view.get("[data-testid='new-session-profile']").text()).toContain("No profile");
        await type(view, "Hello");
        await pressEnter(view);
        expect(lastCreateCall()[1].harnessProfileId).toBe(NO_PROFILE);
      });

      it("asks for the agents and models on the session's profile", async () => {
        harnesses.value = [withProfiles];
        seedProfiles([profile("work", "Work", true), profile("local", "Local")]);
        rememberFolder({ kind: "directory", path: "/home/me/notes" });
        const view = await mountComposer();

        expect(catalogRequests.at(-1)).toEqual({ harnessType: "opencode", directory: "/home/me/notes", profile: "work" });

        await view.get("[data-testid='new-session-profile']").trigger("click");
        await flushPromises();
        await inDocument().get("[data-testid='new-session-profile-none']").trigger("click");
        await flushPromises();

        expect(catalogRequests.at(-1)).toEqual({ harnessType: "opencode", directory: "/home/me/notes", profile: NO_PROFILE });
      });
    });

    it("shows the project on the more chip when it isn't Scratch", async () => {
      mocks.search.value = { projectId: "project-fleet", source: undefined };
      const view = await mountComposer();

      expect(view.get("[data-testid='new-session-more-chip']").text()).toContain("Fleet Core");
    });

    it("names the new worktree's branch from the message", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();

      await type(view, "Fix the login redirect");

      expect(view.get("[data-testid='new-session-plan']").text())
        .toContain("New worktree rocket-worktrees/fleet-fix-login-redirect on fleet/fix-login-redirect");
    });
  });

  describe("base chip", () => {
    async function openBaseMenu(view: VueWrapper): Promise<void> {
      await view.get("[data-testid='new-session-base-chip']").trigger("click");
      await flushPromises();
    }

    function branchOption(name: string) {
      const option = inDocument().findAll("[role='option']")
        .find((candidate) => candidate.find(".ns-option__title").text().replace(/ · .*$/, "") === name);
      if (!option) {
        throw new Error(`No branch option "${name}"`);
      }
      return option;
    }

    it("says where a new worktree starts", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();

      expect(view.get("[data-testid='new-session-base-chip']").text()).toBe("from origin/main");
      await type(view, "Fix the login redirect");
      expect(view.get("[data-testid='new-session-plan']").text())
        .toContain("on fleet/fix-login-redirect, from origin/main.");
    });

    it("isn't there for the current checkout", async () => {
      rememberFolder({ kind: "repository", path: rocket.path }, { [rocket.path]: "current" });
      const view = await mountComposer();

      expect(view.find("[data-testid='new-session-base-chip']").exists()).toBe(false);
    });

    it("lists the default first, marked, and the checked-out branch", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();

      await openBaseMenu(view);

      const titles = inDocument().findAll("[role='option'] .ns-option__title").map((title) => title.text());
      expect(titles[0]).toBe("origin/main · default");
      expect(titles).toContain("feature/x · checked out");
      expect(branchOption("origin/main").attributes("aria-selected")).toBe("true");
    });

    it("starts from a chosen branch, unfetched and under a typed name when asked", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();

      await openBaseMenu(view);
      await branchOption("origin/release/2.0").trigger("click");
      await flushPromises();
      expect(view.get("[data-testid='new-session-base-chip']").text()).toBe("from origin/release/2.0");

      await openBaseMenu(view);
      await inDocument().get("[data-testid='new-session-base-fetch']").trigger("click");
      await inDocument().get("#new-session-branch-name").setValue("hotfix/login");
      await flushPromises();

      expect(view.get("[data-testid='new-session-base-chip']").text()).toBe("from origin/release/2.0 · no fetch");
      await type(view, "Fix the login redirect");
      expect(view.get("[data-testid='new-session-plan']").text())
        .toContain("on hotfix/login, from origin/release/2.0 as last fetched.");

      await pressEnter(view);

      const [, options] = lastCreateCall();
      expect(options).toMatchObject({ branch: "hotfix/login" });
      expect((options.source as { input: Record<string, unknown> }).input).toMatchObject({
        branch: "hotfix/login",
        baseBranch: "origin/release/2.0",
        fetchOrigin: false,
      });
    });

    it("reopens with the chosen branch highlighted, even after searching for it", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();

      await openBaseMenu(view);
      await inDocument().get("input[aria-label='Search branches']").setValue("release");
      await branchOption("origin/release/2.0").trigger("click");
      await flushPromises();
      await openBaseMenu(view);

      const highlighted = inDocument().get("[role='option'][data-highlighted]");
      expect(highlighted.find(".ns-option__title").text()).toBe("origin/release/2.0");
    });

    it("sends no base for the default, so the server's rules apply", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();

      await openBaseMenu(view);
      await branchOption("main").trigger("click");
      await openBaseMenu(view);
      await branchOption("origin/main").trigger("click");
      await type(view, "Fix the login redirect");
      await pressEnter(view);

      const input = (lastCreateCall()[1].source as { input: Record<string, unknown> }).input;
      expect(input).not.toHaveProperty("baseBranch");
      expect(input).not.toHaveProperty("fetchOrigin");
    });

    it("can't turn fetching off for a local branch", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();

      await openBaseMenu(view);
      await branchOption("main").trigger("click");
      await openBaseMenu(view);

      expect(inDocument().get("[data-testid='new-session-base-fetch']").attributes("disabled")).toBeDefined();
    });

    it("forgets the base when the folder changes", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();

      await openBaseMenu(view);
      await branchOption("origin/release/2.0").trigger("click");
      await openFolderMenu(view);
      await folderOption("comet").trigger("click");

      expect(view.get("[data-testid='new-session-base-chip']").text()).toBe("from origin/main");
    });

    it("warns when the current checkout isn't on the default branch", async () => {
      rememberFolder({ kind: "repository", path: rocket.path }, { [rocket.path]: "current" });
      const view = await mountComposer();

      expect(view.get("[data-testid='new-session-plan']").text())
        .toContain("Works directly in ~/src/rocket on feature/x. That's not main.");
      expect(view.get(".new-session__plan-warn").text()).toBe(". That's not main.");
    });
  });

  describe("agent and model", () => {
    const copilotCatalog: HarnessCatalog = {
      supported: true,
      agents: [
        { name: "loom", mode: "primary", description: "Main orchestrator", model: { providerID: "github-copilot", modelID: "claude-opus-4.7" } },
        { name: "tapestry", mode: "primary", model: { providerID: "github-copilot", modelID: "claude-sonnet-5" } },
        { name: "build", mode: "primary" },
        { name: "title", mode: "primary", hidden: true },
      ],
      providers: [
        {
          id: "github-copilot",
          name: "GitHub Copilot",
          models: [
            { id: "claude-opus-4.7", name: "Claude Opus 4.7" },
            { id: "claude-sonnet-5", name: "Claude Sonnet 5" },
            { id: "claude-haiku-4.5", name: "Claude Haiku 4.5" },
          ],
        },
      ],
      defaultAgent: "loom",
      defaultModel: null,
    };

    function agentChip(view: VueWrapper) {
      return view.get("[data-testid='new-session-agent']");
    }

    function modelChip(view: VueWrapper) {
      return view.get("[data-testid='new-session-model']");
    }

    async function pick(view: VueWrapper, chip: "agent" | "model", label: string): Promise<void> {
      await (chip === "agent" ? agentChip(view) : modelChip(view)).trigger("click");
      await flushPromises();
      const item = view.findAll(".selector-dropdown__item").find((candidate) => candidate.text().startsWith(label));
      if (!item) {
        throw new Error(`No ${chip} option "${label}"`);
      }
      await item.trigger("click");
      await flushPromises();
    }

    it("asks the harness about the chosen folder and says what Default is", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      catalog.value = copilotCatalog;
      const view = await mountComposer();

      expect(catalogRequests.at(-1)).toEqual({ harnessType: "opencode", directory: rocket.path });
      expect(agentChip(view).text()).toBe("Default (loom)");
      expect(modelChip(view).text()).toBe("Default (Claude Opus 4.7)");
    });

    it("Default's model follows the picked agent", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      catalog.value = copilotCatalog;
      const view = await mountComposer();

      await pick(view, "agent", "tapestry");

      expect(agentChip(view).text()).toBe("tapestry");
      expect(modelChip(view).text()).toBe("Default (Claude Sonnet 5)");
    });

    it("starts the session with the picked agent and model", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      catalog.value = copilotCatalog;
      const view = await mountComposer();

      await pick(view, "agent", "tapestry");
      await pick(view, "model", "Claude Haiku 4.5");
      await type(view, "Fix the login redirect");
      await pressEnter(view);

      expect(lastCreateCall()[1]).toMatchObject({
        agent: "tapestry",
        model: { providerID: "github-copilot", modelID: "claude-haiku-4.5" },
      });
    });

    it("names no agent or model when both are Default", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      catalog.value = copilotCatalog;
      const view = await mountComposer();

      await type(view, "Fix the login redirect");
      await pressEnter(view);

      expect(lastCreateCall()[1]).not.toHaveProperty("agent");
      expect(lastCreateCall()[1]).not.toHaveProperty("model");
    });

    it("remembers the choice for the folder, not for others", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      catalog.value = copilotCatalog;
      const first = await mountComposer();
      await pick(first, "agent", "tapestry");
      await pick(first, "model", "Claude Haiku 4.5");
      await type(first, "Fix the login redirect");
      await pressEnter(first);
      first.unmount();
      wrapper = null;

      const again = await mountComposer();
      expect(agentChip(again).text()).toBe("tapestry");
      expect(modelChip(again).text()).toBe("Claude Haiku 4.5");

      await openFolderMenu(again);
      await folderOption("comet").trigger("click");
      await flushPromises();
      expect(agentChip(again).text()).toBe("Default (loom)");
      expect(modelChip(again).text()).toBe("Default (Claude Opus 4.7)");
    });

    it("drops a remembered agent the folder no longer has", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const stored = JSON.parse(localStorage.getItem(NEW_SESSION_DEFAULTS_KEY)!) as Record<string, unknown>;
      localStorage.setItem(NEW_SESSION_DEFAULTS_KEY, JSON.stringify({
        ...stored,
        choiceByFolder: { [rocket.path]: { opencode: { agent: "retired", model: "" } } },
      }));
      catalog.value = copilotCatalog;
      const view = await mountComposer();

      expect(agentChip(view).text()).toBe("Default (loom)");
      await type(view, "Fix the login redirect");
      await pressEnter(view);
      expect(lastCreateCall()[1]).not.toHaveProperty("agent");
    });

    it("offers no choice, and sends none, when the harness can't list its agents", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const stored = JSON.parse(localStorage.getItem(NEW_SESSION_DEFAULTS_KEY)!) as Record<string, unknown>;
      localStorage.setItem(NEW_SESSION_DEFAULTS_KEY, JSON.stringify({
        ...stored,
        choiceByFolder: { [rocket.path]: { opencode: { agent: "tapestry", model: "" } } },
      }));
      catalog.value = { supported: false, agents: [], providers: [], defaultAgent: null, defaultModel: null };
      const view = await mountComposer();

      expect(view.find("[data-testid='new-session-agent']").exists()).toBe(false);
      expect(view.find("[data-testid='new-session-model']").exists()).toBe(false);
      await type(view, "Fix the login redirect");
      await pressEnter(view);
      expect(lastCreateCall()[1]).not.toHaveProperty("agent");
    });

    it("asks about an existing worktree as it is", async () => {
      worktrees.value = [{ path: "/home/me/src/rocket-wt", branch: "fleet/other", commitHash: null }];
      rememberFolder({ kind: "repository", path: rocket.path }, { [rocket.path]: "/home/me/src/rocket-wt" });
      catalog.value = copilotCatalog;
      await mountComposer();

      expect(catalogRequests.at(-1)).toEqual({ harnessType: "opencode", directory: "/home/me/src/rocket-wt" });
    });
  });

  describe("keyboard", () => {
    it("Enter creates the session with the message as its first prompt", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();

      await type(view, "Fix the login redirect");
      await pressEnter(view);

      expect(mocks.createSession).toHaveBeenCalledTimes(1);
      const [directory, options] = lastCreateCall();
      expect(directory).toBe(rocket.path);
      expect(options).toMatchObject({
        initialPrompt: "Fix the login redirect",
        isolationStrategy: "worktree",
        branch: "fleet/fix-login-redirect",
        harnessType: "opencode",
      });
      expect(mocks.navigate).toHaveBeenCalledWith({
        to: "/sessions/$id",
        params: { id: "session-1" },
        search: { instanceId: "instance-1", parentSessionId: undefined },
      });
    });

    it("Shift+Enter is a new line, not a send", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();

      await type(view, "First line");
      await pressEnter(view, { shiftKey: true });

      expect(mocks.createSession).not.toHaveBeenCalled();
    });

    it("Enter with an empty message does nothing", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();

      await pressEnter(view);

      expect(mocks.createSession).not.toHaveBeenCalled();
      expect(view.get("[data-testid='create-session-submit']").attributes("disabled")).toBeDefined();
    });

    it("says why no harness is ready and won't send until one is", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      harnesses.value = [];
      noHarnessReason.value = "OpenCode isn't installed: Fleet couldn't find opencode on PATH or in the folders its installer uses.";
      const view = await mountComposer();
      await type(view, "Fix the flaky test");

      await pressEnter(view);

      expect(view.get("[data-testid='new-session-no-harness']").text()).toContain("OpenCode isn't installed");
      expect(view.get("[data-testid='create-session-submit']").attributes("disabled")).toBeDefined();
      expect(mocks.createSession).not.toHaveBeenCalled();

      await view.get("[data-testid='new-session-no-harness'] button").trigger("click");
      expect(useHarnessSetupStore().isOpen).toBe(true);
      expect(useHarnessSetupStore().step).toBe("harnesses");
      expect(mocks.navigate).not.toHaveBeenCalled();
    });

    it("in cloud mode, where a harness can't be set up from Fleet, points to Settings instead", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      harnesses.value = [];
      noHarnessReason.value = "OpenCode isn't installed.";
      useAppShellStore().config = { ...useAppShellStore().config, cloudMode: true };
      const view = await mountComposer();

      const link = view.get("[data-testid='new-session-no-harness'] button");
      expect(link.text()).toBe("Open Settings → Harnesses");
      await link.trigger("click");

      expect(useHarnessSetupStore().isOpen).toBe(false);
      expect(useSettingsNav().activeSection.value).toBe("harnesses");
      expect(mocks.navigate).toHaveBeenCalledWith({ to: "/settings" });
    });

    it("Esc never leaves the page", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();
      await type(view, "Half a thought");

      await textarea(view).trigger("keydown", { key: "Escape" });
      await openFolderMenu(view);
      await inDocument().get("input[aria-label='Search repositories']").trigger("keydown", { key: "Escape" });
      await flushPromises();

      expect(mocks.navigate).not.toHaveBeenCalled();
      expect(textarea(view).element.value).toBe("Half a thought");
    });

    it("the first time, Enter asks where to run instead of creating", async () => {
      const view = await mountComposer();
      expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("Choose a folder");

      await type(view, "Fix the login redirect");
      await pressEnter(view);

      expect(mocks.createSession).not.toHaveBeenCalled();
      expect(inDocument().find("input[aria-label='Search repositories']").exists()).toBe(true);
    });
  });

  describe("quick chat", () => {
    it("choosing No folder creates nothing; Enter creates a chat with the message", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();

      await openFolderMenu(view);
      await folderOption("No folder").trigger("click");
      await flushPromises();

      expect(mocks.createSession).not.toHaveBeenCalled();
      expect(textarea(view).attributes("placeholder")).toBe("Ask anything…");

      await type(view, "What is a monad?");
      await pressEnter(view);

      const [directory, options] = lastCreateCall();
      expect(directory).toBeUndefined();
      expect(options).toMatchObject({
        initialPrompt: "What is a monad?",
        source: { key: { providerId: "builtin.quickchat" } },
      });
    });
  });

  describe("GitHub preset", () => {
    const issue = createGitHubSessionSourcePreset({
      sourceType: "github-issue",
      owner: "acme",
      repo: "rocket",
      number: 42,
      title: "Login redirect loops",
      body: null,
      htmlUrl: "https://github.com/acme/rocket/issues/42",
      repoFullName: "acme/rocket",
      suggestedBranch: "fix/issue-42",
    });

    it("attaches the issue, picks its repository and a new worktree", async () => {
      rememberFolder({ kind: "repository", path: comet.path }, { [rocket.path]: "current" });
      useWorkspaceUiStore().setNewSessionInitialSource(issue);
      const view = await mountComposer();

      expect(view.get("[data-testid='new-session-github-attachment']").text()).toContain("#42");
      expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("rocket");
      expect(view.get("[data-testid='new-session-workspace-chip']").text()).toContain("New worktree");
      expect(view.get("[data-testid='new-session-plan']").text()).toContain("on fix/issue-42");
    });

    it("can start with only the issue", async () => {
      useWorkspaceUiStore().setNewSessionInitialSource(issue);
      const view = await mountComposer();

      await view.get("[data-testid='create-session-submit']").trigger("click");
      await flushPromises();

      const [, options] = lastCreateCall();
      expect(options).toMatchObject({
        title: "Login redirect loops",
        branch: "fix/issue-42",
        source: { key: { providerId: "builtin.github" }, input: { number: 42, repositoryPath: rocket.path } },
      });
      expect(options).not.toHaveProperty("initialPrompt");
      expect(useWorkspaceUiStore().newSessionInitialSource).toBeNull();
    });

    it("offers no folder-less choices while the issue is attached", async () => {
      useWorkspaceUiStore().setNewSessionInitialSource(issue);
      const view = await mountComposer();

      await openFolderMenu(view);
      const labels = inDocument().findAll("[role='option']").map((option) => option.text());

      expect(labels.some((label) => label.includes("rocket"))).toBe(true);
      expect(labels.some((label) => label.includes("No folder"))).toBe(false);
      expect(labels.some((label) => label.includes("Browse for a folder"))).toBe(false);
    });

    it("can be removed", async () => {
      useWorkspaceUiStore().setNewSessionInitialSource(issue);
      const view = await mountComposer();

      await view.get("[aria-label='Remove issue #42']").trigger("click");

      expect(view.find("[data-testid='new-session-github-attachment']").exists()).toBe(false);
      expect(useWorkspaceUiStore().newSessionInitialSource).toBeNull();
      expect(view.get("[data-testid='create-session-submit']").attributes("disabled")).toBeDefined();
    });
  });

  it("starts without a message", async () => {
    rememberFolder({ kind: "directory", path: "/tmp/notes" });
    const view = await mountComposer();

    await view.get("[data-testid='create-session-without-message']").trigger("click");
    await flushPromises();

    const [directory, options] = lastCreateCall();
    expect(directory).toBe("/tmp/notes");
    expect(options).not.toHaveProperty("initialPrompt");
  });

  it("uses a typed folder", async () => {
    rememberFolder({ kind: "repository", path: rocket.path });
    const view = await mountComposer();

    await openFolderMenu(view);
    await folderOption("Browse for a folder").trigger("click");
    await flushPromises();
    const input = inDocument().get<HTMLInputElement>("#new-session-directory");
    await input.setValue("/srv/scratch");
    await input.trigger("keydown", { key: "Enter" });
    await flushPromises();

    expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("/srv/scratch");
    expect(view.find("[data-testid='new-session-workspace-chip']").exists()).toBe(false);
  });

  describe("browsing for a folder", () => {
    async function browseTo(view: VueWrapper, path: string): Promise<void> {
      await openFolderMenu(view);
      await folderOption("Browse for a folder").trigger("click");
      await flushPromises();
      const input = inDocument().get<HTMLInputElement>("#new-session-directory");
      await input.setValue(path);
      await input.trigger("keydown", { key: "Enter" });
      await flushPromises();
    }

    function button(label: string) {
      const found = inDocument().findAll("button").find((candidate) => candidate.text() === label);
      if (!found) {
        throw new Error(`No button "${label}"`);
      }
      return found;
    }

    it("uses a git folder as a repository, so it can get a new worktree", async () => {
      mocks.inspectFolder.mockResolvedValue({ path: "/home/me/deep/agent-playbook", exists: true, isGitRepo: true, isWithinRoots: true });
      const view = await mountComposer();

      await browseTo(view, "/home/me/deep/agent-playbook");
      await type(view, "Tidy the README");
      await pressEnter(view);

      expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("agent-playbook");
      const [directory, options] = lastCreateCall();
      expect(directory).toBe("/home/me/deep/agent-playbook");
      expect(options.isolationStrategy).toBe("worktree");
      expect(options.source).toMatchObject({
        key: { providerId: "builtin.repository" },
        input: { repositoryPath: "/home/me/deep/agent-playbook", isolationStrategy: "worktree" },
      });
    });

    it("offers to add a folder outside the workspace roots, then uses it", async () => {
      mocks.inspectFolder.mockResolvedValue({ path: "C:\\source\\agent-playbook", exists: true, isGitRepo: true, isWithinRoots: false });
      const view = await mountComposer();

      await browseTo(view, "c:/source/agent-playbook");

      expect(inDocument().get("[data-testid='new-session-add-folder']").text()).toContain("agent-playbook isn't in Fleet yet");
      expect(mocks.addFolderToFleet).not.toHaveBeenCalled();

      await button("Add repository").trigger("click");
      await flushPromises();

      expect(mocks.addFolderToFleet).toHaveBeenCalledWith("C:\\source\\agent-playbook");
      expect(mocks.refreshRepositories).toHaveBeenCalled();
      expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("agent-playbook");
      expect(view.find("[data-testid='new-session-workspace-chip']").exists()).toBe(true);
    });

    it("says so when the folder doesn't exist", async () => {
      mocks.inspectFolder.mockResolvedValue({ path: "/nope", exists: false, isGitRepo: false, isWithinRoots: false });
      const view = await mountComposer();

      await browseTo(view, "/nope");

      expect(inDocument().get("[role='alert']").text()).toBe("There's no folder at that path.");
      expect(view.get("[data-testid='new-session-folder-chip']").text()).not.toContain("/nope");
    });
  });

  it("remembers the choices a session was created with", async () => {
    rememberFolder({ kind: "repository", path: rocket.path }, { [rocket.path]: "current" });
    const view = await mountComposer();

    await type(view, "Tidy up");
    await pressEnter(view);

    const stored = JSON.parse(localStorage.getItem(NEW_SESSION_DEFAULTS_KEY) ?? "{}");
    expect(stored.lastFolder).toEqual({ kind: "repository", path: rocket.path });
    expect(stored.workspaceByRepository[rocket.path]).toBe("current");
  });

  it("shows the server's error above the box and keeps the message", async () => {
    rememberFolder({ kind: "repository", path: rocket.path });
    mocks.createSession.mockImplementation(async () => {
      createError.value = "Couldn't create the worktree: 'x' is not a valid branch name";
      throw new Error(createError.value);
    });
    const view = await mountComposer();

    await type(view, "Fix it");
    await pressEnter(view);

    expect(view.get("[data-testid='new-session-error']").text()).toContain("Couldn't create the worktree");
    expect(textarea(view).element.value).toBe("Fix it");
    expect(mocks.navigate).not.toHaveBeenCalled();
  });

  describe("feels instant", () => {
    function firstMessages(view: VueWrapper) {
      return view.findAll("[data-testid='message-item'][data-role='user']");
    }

    it("shows the message as the conversation's first right after Enter, while the session starts", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      let finishCreate: (value: unknown) => void = () => {};
      mocks.createSession.mockImplementation(() => new Promise((resolve) => { finishCreate = resolve; }));
      const view = await mountComposer();

      await type(view, "Fix the login redirect");
      await pressEnter(view);

      expect(firstMessages(view)).toHaveLength(1);
      expect(firstMessages(view)[0].text()).toContain("Fix the login redirect");
      expect(textarea(view).element.value).toBe("");
      expect(useWorkspaceUiStore().newSessionDraftRow).toMatchObject({ title: "Fix the login redirect", isStarting: true });

      finishCreate({ instanceId: "instance-1", workspaceId: "workspace-1", session: { id: "session-1", title: "Fix the login redirect", time: { created: 0, updated: 0 }, tags: [] } });
      await flushPromises();
      expect(mocks.navigate).toHaveBeenCalled();
    });

    it("hands the session page its first message and its sidebar row before opening it", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const seen: { prompts: string[]; rowIds: string[]; rowKey?: string } = { prompts: [], rowIds: [] };
      mocks.navigate.mockImplementation(async () => {
        seen.prompts = useSentPrompts("session-1").sentPrompts.value.map((prompt) => prompt.body);
        seen.rowIds = useSessionsStore().sessions.map((session) => session.session.id);
        seen.rowKey = useWorkspaceUiStore().sessionRowKeys["session-1"];
      });
      const view = await mountComposer();
      const draftRowKey = useWorkspaceUiStore().newSessionDraftRow?.key;

      await type(view, "Fix the login redirect");
      await pressEnter(view);

      expect(seen.prompts).toEqual(["Fix the login redirect"]);
      expect(seen.rowIds[0]).toBe("session-1");
      expect(useSessionsStore().sessions[0]).toMatchObject({ projectId: "scratch", projectName: "Scratch", sessionStatus: "active" });
      expect(seen.rowKey).toBe(draftRowKey);
      expect(useWorkspaceUiStore().newSessionDraftRow).toBeNull();
    });

    it("titles the session after the first message", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();

      await type(view, "Fix the login redirect\nIt loops on /login");
      await pressEnter(view);

      expect(lastCreateCall()[1]).toMatchObject({ title: "Fix the login redirect" });
    });

    it("seeds no message when starting without one", async () => {
      rememberFolder({ kind: "directory", path: "/tmp/notes" });
      const view = await mountComposer();

      await view.get("[data-testid='create-session-without-message']").trigger("click");
      await flushPromises();

      expect(useSentPrompts("session-1").sentPrompts.value).toHaveLength(0);
      expect(useSessionsStore().sessions[0]?.sessionStatus).toBe("idle");
    });

    it("puts the message back in the box when the session can't be created", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      mocks.createSession.mockImplementation(async () => {
        createError.value = "Couldn't create the worktree";
        throw new Error(createError.value);
      });
      const view = await mountComposer();

      await type(view, "Fix it");
      await pressEnter(view);

      expect(firstMessages(view)).toHaveLength(0);
      expect(textarea(view).element.value).toBe("Fix it");
      expect(useWorkspaceUiStore().newSessionDraftRow).toMatchObject({ title: "Fix it", isStarting: false });
      expect(useSessionsStore().sessions).toHaveLength(0);
    });

    it("keeps the draft when you leave and come back", async () => {
      rememberFolder({ kind: "repository", path: rocket.path }, { [rocket.path]: "current" });
      const first = await mountComposer();
      await type(first, "Half a thought");
      await first.get("[data-testid='new-session-workspace-chip']").trigger("click");
      await flushPromises();
      const newWorktree = inDocument().findAll("[role='menuitem']").find((item) => item.text().includes("New worktree"));
      await newWorktree?.trigger("click");
      await flushPromises();
      first.unmount();
      wrapper = null;

      expect(useWorkspaceUiStore().newSessionDraftRow?.title).toBe("Half a thought");

      const again = await mountComposer();
      expect(textarea(again).element.value).toBe("Half a thought");
      expect(again.get("[data-testid='new-session-workspace-chip']").text()).toContain("New worktree");
    });

    it("drops an empty draft when you leave", async () => {
      const view = await mountComposer();
      expect(useWorkspaceUiStore().newSessionDraftRow).not.toBeNull();

      view.unmount();
      wrapper = null;

      expect(useWorkspaceUiStore().newSessionDraftRow).toBeNull();
    });

    it("a project's + moves a kept draft to that project", async () => {
      const first = await mountComposer();
      await type(first, "Half a thought");
      first.unmount();
      wrapper = null;

      mocks.search.value = { projectId: "project-fleet", source: undefined };
      const again = await mountComposer();

      expect(textarea(again).element.value).toBe("Half a thought");
      expect(useWorkspaceUiStore().newSessionDraftRow?.projectId).toBe("project-fleet");
    });
  });
});
