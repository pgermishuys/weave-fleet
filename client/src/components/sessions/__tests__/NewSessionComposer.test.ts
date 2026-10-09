import { DOMWrapper, flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { computed, defineComponent, h, ref, shallowRef, watchEffect, type Ref } from "vue";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api, NO_PROFILE } from "@/api/client";
import type { BranchInfo, HarnessCatalog, HarnessInfo, HarnessProfile, RepositoryDetail, ScannedRepository, WorktreeInfo } from "@/api/client";
import NewSessionComposer from "@/components/sessions/NewSessionComposer.vue";
import { toAgentOptions } from "@/composables/use-agents";
import { toModelOptions } from "@/composables/use-models";
import { NEW_SESSION_DEFAULTS_KEY, newSessionDefaultsKey } from "@/composables/use-new-session-defaults";
import { clearSentPrompts, useSentPrompts } from "@/composables/use-send-prompt";
import { createGitHubSessionSourcePreset } from "@/lib/github-session-source";
import { useHarnessProfilesStore } from "@/stores/harness-profiles";
import { provideMachineTarget, targetFor } from "@/lib/machine-target";
import { loadSessionMachines, type MachineConnection } from "@/lib/machines";
import { useMachinesStore } from "@/stores/machines";
import { usePreferencesStore } from "@/stores/preferences";
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
  listWorkspaceRoots: vi.fn(),
  createFolder: vi.fn(),
  cloneRepository: vi.fn(),
  listFolder: vi.fn(),
  newFolderDefaults: vi.fn(),
  refreshGitHubRepos: vi.fn(),
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
  listWorkspaceRoots: mocks.listWorkspaceRoots,
  createFolder: mocks.createFolder,
  cloneRepository: mocks.cloneRepository,
  listFolder: mocks.listFolder,
  newFolderDefaults: mocks.newFolderDefaults,
}));

/** What the folder box finds on disk: each folder's folders, `true` for a repository. */
const disk: Record<string, Record<string, boolean>> = {
  "/home/me/src": { clients: false, comet: true, rocket: true },
  "/home/me/src/clients": { "northwind-api": true },
  "/home/me/deep": { "agent-playbook": true },
  "/srv": { scratch: false },
  "c:/source": { "agent-playbook": true },
};

function fakeListing(typed: string) {
  const path = typed.replace(/^~/, "/home/me").replace(/\\/g, "/");
  const folders = disk[path];
  let nearest: string | null = null;
  for (let above = path.slice(0, path.lastIndexOf("/")); above && !nearest; above = above.slice(0, above.lastIndexOf("/"))) {
    nearest = disk[above] ? above : null;
  }
  return {
    path,
    exists: folders !== undefined,
    nearestExisting: folders ? null : nearest,
    entries: Object.entries(folders ?? {}).map(([name, isGitRepo]) => ({ name, path: `${path}/${name}`, isGitRepo })),
  };
}

const gitHubRepos = shallowRef<{ id: number; full_name: string; name: string }[]>([]);

vi.mock("@/plugins/builtin/github/composables/use-github-repos", () => ({
  useGitHubRepos: () => ({ repos: gitHubRepos, refresh: mocks.refreshGitHubRepos }),
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

// The # picker's GitHub answers; the composer is what's under test here.
const picker = vi.hoisted(() => ({ asked: [] as Array<{ repository: unknown; query: unknown }>, items: [] as unknown[], loading: false }));
vi.mock("@/composables/use-github-picker", async () => {
  const { computed, toValue } = await import("vue");
  return {
    useGitHubPicker: (repository: () => unknown, query: () => unknown) => ({
      groups: computed(() => {
        const asked = { repository: toValue(repository), query: toValue(query) };
        if (asked.query === null) return [];
        picker.asked.push(asked);
        return [{ label: "Assigned to you", items: picker.items }];
      }),
      isLoading: computed(() => picker.loading),
      error: computed(() => null),
    }),
  };
});

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

/** Folder chip → Open or create a folder… */
async function openPathBox(view: VueWrapper): Promise<void> {
  await openFolderMenu(view);
  await folderOption("Open or create a folder").trigger("click");
  await flushPromises();
}

function pathInput() {
  return inDocument().get<HTMLInputElement>("[data-testid='new-session-path-input']");
}

async function typePath(text: string): Promise<void> {
  await pathInput().setValue(text);
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
  mocks.listWorkspaceRoots.mockReset().mockResolvedValue(["/home/me/src", "/home/me/work"]);
  mocks.createFolder.mockReset().mockImplementation(async (path: string, git: boolean) => ({
    path,
    isGitRepo: git,
    addedToFleet: false,
    warning: null,
  }));
  mocks.cloneRepository.mockReset();
  mocks.listFolder.mockReset().mockImplementation(async (path: string) => fakeListing(path));
  mocks.newFolderDefaults.mockReset().mockResolvedValue({ firstBranch: "main", home: "/home/me" });
  mocks.refreshGitHubRepos.mockReset().mockResolvedValue(undefined);
  gitHubRepos.value = [];
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

    it("previews the name the worktree naming templates will give it", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();

      await type(view, "Fix the login redirect");

      // A preview: the request carries no branch, and the server does the naming.
      expect(view.get("[data-testid='new-session-plan']").text())
        .toContain("New worktree ~/src/rocket-worktrees/fleet-fix-login-redirect on fleet/fix-login-redirect");
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
        harnessType: "opencode",
      });
      // Nobody typed a branch, so the naming templates name it on the server.
      expect(options.branch).toBeUndefined();
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
      await inDocument().get("input[aria-label='Search, or create a folder']").trigger("keydown", { key: "Escape" });
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
      expect(inDocument().find("input[aria-label='Search, or create a folder']").exists()).toBe(true);
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

  describe("# picker", () => {
    const flicker = {
      kind: "issue", owner: "acme", repo: "rocket", number: 318, title: "Session list flickers", url: "https://github.com/acme/rocket/issues/318",
      state: "open", isDraft: false, author: "pat", authorAvatarUrl: null, createdAt: "", updatedAt: "", comments: 0, labels: [],
      headRef: null, baseRef: null, additions: null, deletions: null, checks: null, reviewDecision: null, mergeable: null, reviewers: [], assignees: [],
    };

    beforeEach(() => {
      picker.asked.length = 0;
      picker.items = [flicker];
      picker.loading = false;
      rememberFolder({ kind: "repository", path: rocket.path });
      repositoryDetail.value = {
        ...repositoryDetail.value!,
        // The server sends the URL only; the composer reads the repository from it.
        remotes: [{ name: "upstream", url: "https://gitlab.com/x/y.git", github: null }, { name: "origin", url: "https://github.com/acme/rocket", github: null }],
      };
    });

    it("opens on # and asks the folder's repository", async () => {
      const view = await mountComposer();
      await type(view, "Fix #31");

      expect(view.find("[data-testid='github-item-picker']").exists()).toBe(true);
      expect(picker.asked.at(-1)).toEqual({ repository: { owner: "acme", repo: "rocket" }, query: "31" });
      expect(view.get("[data-testid='github-item-picker-row']").text()).toContain("#318");
    });

    it("Enter attaches the item, drops the #query and starts in a new worktree", async () => {
      const view = await mountComposer();
      await type(view, "Fix #31");
      await pressEnter(view);

      expect(mocks.createSession).not.toHaveBeenCalled();
      expect(view.find("[data-testid='github-item-picker']").exists()).toBe(false);
      expect(view.get("[data-testid='new-session-github-attachment']").text()).toContain("#318");
      expect(textarea(view).element.value).toBe("Fix ");
      expect(view.get("[data-testid='new-session-workspace-chip']").text()).toContain("New worktree");
    });

    it("Esc closes it and leaves the message alone", async () => {
      const view = await mountComposer();
      await type(view, "Fix #31");
      await textarea(view).trigger("keydown", { key: "Escape" });

      expect(view.find("[data-testid='github-item-picker']").exists()).toBe(false);
      expect(textarea(view).element.value).toBe("Fix #31");
      expect(view.find("[data-testid='new-session-github-attachment']").exists()).toBe(false);
    });

    it("Enter waits while GitHub is still answering", async () => {
      picker.items = [];
      picker.loading = true;
      const view = await mountComposer();
      await type(view, "Fix #31");
      await pressEnter(view);

      expect(mocks.createSession).not.toHaveBeenCalled();
    });

    it("Enter sends the message when there's nothing to pick", async () => {
      picker.items = [];
      const view = await mountComposer();
      await type(view, "Fix #999");
      await pressEnter(view);

      expect(mocks.createSession).toHaveBeenCalled();
    });

    it("stays shut for # inside a word", async () => {
      const view = await mountComposer();
      await type(view, "Port it to C#");

      expect(view.find("[data-testid='github-item-picker']").exists()).toBe(false);
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
      expect(labels.some((label) => label.includes("Open or create a folder"))).toBe(false);
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

    await openPathBox(view);
    await typePath("/srv/scratch");
    await pathInput().trigger("keydown", { key: "Enter" });
    await flushPromises();

    expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("/srv/scratch");
    expect(view.find("[data-testid='new-session-workspace-chip']").exists()).toBe(false);
  });

  describe("the folder box", () => {
    function pathRows(): string[] {
      return inDocument().findAll("[id$='-path'] [role='option']").map((row) => row.text());
    }

    it("starts in the location and lists what's there", async () => {
      const view = await mountComposer();

      await openPathBox(view);

      expect(pathInput().element.value).toBe("~/src/");
      expect(mocks.listFolder).toHaveBeenLastCalledWith("~/src", api, expect.anything());
      const rows = pathRows();
      expect(rows[0]).toContain("Open src");
      expect(rows.slice(1).map((row) => row.split(/Folder|Repository/)[0]!.trim())).toEqual(["clients", "comet", "rocket"]);
    });

    it("writes ~ only for the machine's own home folder, which the server expands the same way", async () => {
      mocks.newFolderDefaults.mockResolvedValue({ firstBranch: "main", home: "/home/someone-else" });
      const view = await mountComposer();

      await openPathBox(view);

      expect(pathInput().element.value).toBe("/home/me/src/");
      expect(mocks.listFolder).toHaveBeenLastCalledWith("/home/me/src", api, expect.anything());
    });

    it("puts what's there before Create, so Tab and Enter finish a name instead of making it", async () => {
      const view = await mountComposer();

      await openPathBox(view);
      await typePath("~/src/cl");

      const rows = pathRows();
      expect(rows[0]).toContain("clients");
      expect(rows[0]).toContain("Tab");
      expect(rows.at(-1)).toContain("Create cl");

      await pathInput().trigger("keydown", { key: "Tab" });
      await flushPromises();

      expect(pathInput().element.value).toBe("~/src/clients/");
      expect(pathRows()[0]).toContain("Open clients");
      expect(pathRows()[1]).toContain("northwind-api");
    });

    it("creates a folder inside a folder, typed with \\ or /, on the branch asked for", async () => {
      const view = await mountComposer();

      await openPathBox(view);
      await typePath("~/src/clients\\acme-portal");

      expect(mocks.listFolder).toHaveBeenLastCalledWith("~/src/clients", api, expect.anything());
      const create = inDocument().get("[data-testid='new-session-path-create']");
      expect(create.text()).toContain("Create acme-portal");
      expect(create.text()).toContain("in ~/src/clients");
      const preview = inDocument().get("[data-testid='new-session-path-preview']");
      expect(preview.text()).toContain("~/src/clients");
      expect(preview.text()).toContain("acme-portal");
      expect(preview.text()).toContain("A git repository on main with an empty first commit");

      await inDocument().get("[data-testid='new-session-path-branch']").setValue("trunk");
      await pathInput().trigger("keydown", { key: "Enter" });
      await flushPromises();

      expect(mocks.createFolder).toHaveBeenCalledWith("/home/me/src/clients/acme-portal", true, api, "trunk");
      expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("acme-portal");
      expect(view.find("[data-testid='new-session-folder-new']").exists()).toBe(true);
    });

    it("makes missing folders above it too, and can make an empty folder", async () => {
      const view = await mountComposer();

      await openPathBox(view);
      await typePath("~/src/clients/acme/site");

      expect(inDocument().get("[data-testid='new-session-path-create']").text()).toContain("Create acme/site");
      const preview = inDocument().get("[data-testid='new-session-path-preview']");
      expect(preview.findAll(".ns-folder-tree__row--new").map((row) => row.text())).toEqual(["acme new", "site new"]);

      await inDocument().get("[data-testid='new-session-path-kind-empty']").trigger("click");
      await inDocument().get("[data-testid='new-session-path-create-submit']").trigger("click");
      await flushPromises();

      expect(mocks.createFolder).toHaveBeenCalledWith("/home/me/src/clients/acme/site", false, api, undefined);
      expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("~/src/clients/acme/site");
    });

    it("says which character a folder name can't have, and offers no Create", async () => {
      const view = await mountComposer();

      await openPathBox(view);
      await typePath("~/src/notes?");

      expect(inDocument().get("[data-testid='new-session-path-note']").text()).toBe("“?” can't be in a folder name.");
      expect(inDocument().find("[data-testid='new-session-path-create']").exists()).toBe(false);
    });

    it("uses a git folder as a repository, so it can get a new worktree", async () => {
      mocks.inspectFolder.mockResolvedValue({ path: "/home/me/deep/agent-playbook", exists: true, isGitRepo: true, isWithinRoots: true });
      const view = await mountComposer();

      await openPathBox(view);
      await typePath("/home/me/deep/agent-playbook");
      await pathInput().trigger("keydown", { key: "Enter" });
      await flushPromises();
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

    it("says a folder outside the locations is added to them, and adds it when it's opened", async () => {
      mocks.inspectFolder.mockResolvedValue({ path: "C:\\source\\agent-playbook", exists: true, isGitRepo: true, isWithinRoots: false });
      const view = await mountComposer();

      await openPathBox(view);
      await typePath("c:/source/agent-playbook");

      expect(inDocument().get("[data-testid='new-session-path-adds-location']").text())
        .toContain("Fleet adds agent-playbook to your locations");
      expect(mocks.addFolderToFleet).not.toHaveBeenCalled();

      await pathInput().trigger("keydown", { key: "Enter" });
      await flushPromises();

      expect(mocks.addFolderToFleet).toHaveBeenCalledWith("C:\\source\\agent-playbook", api);
      expect(mocks.refreshRepositories).toHaveBeenCalled();
      expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("agent-playbook");
      expect(view.find("[data-testid='new-session-workspace-chip']").exists()).toBe(true);
    });
  });

  describe("making a folder", () => {
    async function search(view: VueWrapper, text: string) {
      await openFolderMenu(view);
      const input = inDocument().get<HTMLInputElement>("input[aria-label='Search, or create a folder']");
      await input.setValue(text);
      await flushPromises();
      return input;
    }

    it("a name no repository has offers to create it, and Enter does", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();

      const input = await search(view, "recipe box");

      const row = inDocument().get("[data-testid='new-session-folder-create']");
      expect(row.text()).toContain("Create recipe-box");
      expect(row.text()).toContain("~/src/recipe-box · git repository on main");
      expect(row.attributes("data-highlighted")).toBeDefined();

      await input.trigger("keydown", { key: "Enter" });
      await flushPromises();

      expect(mocks.createFolder).toHaveBeenCalledWith("/home/me/src/recipe-box", true, api, undefined);
      expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("recipe-box");
      expect(view.find("[data-testid='new-session-folder-new']").exists()).toBe(true);
      // A new repository gets New worktree like any other; its first commit gives it a base.
      expect(view.find("[data-testid='new-session-workspace-chip']").exists()).toBe(true);
    });

    it("a path makes folders inside folders in the location, typed with / or \\", async () => {
      const view = await mountComposer();

      const input = await search(view, "clients\\acme portal");

      const row = inDocument().get("[data-testid='new-session-folder-create']");
      expect(row.text()).toContain("Create clients/acme-portal");
      expect(row.text()).toContain("~/src/clients/acme-portal");

      await input.trigger("keydown", { key: "Enter" });
      await flushPromises();

      expect(mocks.createFolder).toHaveBeenCalledWith("/home/me/src/clients/acme-portal", true, api, undefined);
    });

    it("says which character a folder name can't have instead of dropping it", async () => {
      const view = await mountComposer();

      await search(view, "notes: draft");

      expect(inDocument().get("[data-testid='new-session-folder-invalid']").text()).toBe("“:” can't be in a folder name.");
      expect(inDocument().find("[data-testid='new-session-folder-create']").exists()).toBe(false);
    });

    it("a path of its own goes to the folder box", async () => {
      const view = await mountComposer();

      await search(view, "/srv/");
      await inDocument().get("[data-testid='new-session-folder-go']").trigger("click");
      await flushPromises();

      expect(pathInput().element.value).toBe("/srv/");
      expect(mocks.listFolder).toHaveBeenLastCalledWith("/srv", api, expect.anything());
    });

    it("never offers to create a repository that's already there", async () => {
      const view = await mountComposer();

      await search(view, "Rocket");

      expect(inDocument().find("[data-testid='new-session-folder-create']").exists()).toBe(false);
      expect(folderOption("rocket").exists()).toBe(true);
    });

    it("puts new folders in the root used last, and remembers a different pick", async () => {
      localStorage.setItem(NEW_SESSION_DEFAULTS_KEY, JSON.stringify({ lastNewFolderRoot: "/home/me/work" }));
      const view = await mountComposer();

      await search(view, "invoices");
      expect(inDocument().get("[data-testid='new-session-folder-create']").text()).toContain("~/work/invoices");

      await inDocument().get("[data-testid='new-session-folder-root']").setValue("/home/me/src");
      await inDocument().get("[data-testid='new-session-folder-create']").trigger("click");
      await flushPromises();

      expect(mocks.createFolder).toHaveBeenCalledWith("/home/me/src/invoices", true, api, undefined);
      expect(JSON.parse(localStorage.getItem(NEW_SESSION_DEFAULTS_KEY) ?? "{}").lastNewFolderRoot).toBe("/home/me/src");
    });

    it("the first time, a new folder goes in the root of the folder in use", async () => {
      rememberFolder({ kind: "repository", path: "/home/me/work/billing" });
      repositories.value = [rocket, comet, { name: "billing", path: "/home/me/work/billing", parentRoot: "/home/me/work" }];
      const view = await mountComposer();

      await search(view, "invoices");

      expect(inDocument().get("[data-testid='new-session-folder-create']").text()).toContain("~/work/invoices");
    });

    it("an existing folder says so and can be used instead", async () => {
      const { FolderExistsError } = await import("@/lib/folder-access");
      mocks.createFolder.mockRejectedValue(new FolderExistsError("/home/me/src/notes already exists.", "/home/me/src/notes"));
      mocks.inspectFolder.mockResolvedValue({ path: "/home/me/src/notes", exists: true, isGitRepo: false, isWithinRoots: true });
      const view = await mountComposer();

      await search(view, "notes");
      await inDocument().get("[data-testid='new-session-folder-create']").trigger("click");
      await flushPromises();

      const alert = inDocument().get("[role='alert']");
      expect(alert.text()).toContain("/home/me/src/notes already exists.");
      await alert.get("button").trigger("click");
      await flushPromises();

      expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("~/src/notes");
    });

    it("keeps the menu open to say the first commit couldn't be made", async () => {
      mocks.createFolder.mockResolvedValue({
        path: "/home/me/src/recipe-box",
        isGitRepo: true,
        addedToFleet: false,
        warning: "Git couldn't make the first commit: empty ident name not allowed New worktree works once the repository has a commit.",
      });
      const view = await mountComposer();

      await search(view, "recipe-box");
      await inDocument().get("[data-testid='new-session-folder-create']").trigger("click");
      await flushPromises();

      expect(inDocument().get("[data-testid='new-session-folder-warning']").text()).toContain("empty ident name");
      expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("recipe-box");
    });

    it("a pasted repository offers a clone, which starting waits for", async () => {
      let finish: (folder: object) => void = () => {};
      let report: (progress: { phase: string; percent: number }) => void = () => {};
      mocks.cloneRepository.mockImplementation((_repository: string, _path: string, onProgress: typeof report) => {
        report = onProgress;
        return new Promise((resolve) => {
          finish = resolve;
        });
      });
      const view = await mountComposer();

      await search(view, "https://github.com/pgermishuys/recipe-box.git");
      const row = inDocument().get("[data-testid='new-session-folder-clone']");
      expect(row.text()).toContain("Clone pgermishuys/recipe-box");
      expect(row.text()).toContain("into ~/src/recipe-box");
      expect(inDocument().find("[data-testid='new-session-folder-create']").exists()).toBe(false);
      expect(inDocument().get(".ns-pop__label").text()).toBe("Repository to clone");

      await row.trigger("click");
      await flushPromises();
      expect(mocks.cloneRepository).toHaveBeenCalledWith("pgermishuys/recipe-box", "/home/me/src/recipe-box", expect.any(Function), api);

      report({ phase: "Receiving objects", percent: 38 });
      await flushPromises();
      expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("Cloning recipe-box… 38%");

      await type(view, "Add a README");
      await pressEnter(view);
      expect(mocks.createSession).not.toHaveBeenCalled();
      expect(view.get("[data-testid='create-session-submit']").attributes("disabled")).toBeDefined();

      finish({ path: "/home/me/src/recipe-box", isGitRepo: true, addedToFleet: false, warning: null });
      await flushPromises();

      expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("recipe-box");
      expect(view.get("[data-testid='create-session-submit']").attributes("disabled")).toBeUndefined();
      await pressEnter(view);
      expect(mocks.createSession).toHaveBeenCalled();
    });

    it("picking another folder while a clone runs lets the clone finish without taking over", async () => {
      rememberFolder({ kind: "repository", path: rocket.path });
      let finish: (folder: object) => void = () => {};
      mocks.cloneRepository.mockImplementation(() => new Promise((resolve) => {
        finish = resolve;
      }));
      const view = await mountComposer();

      await search(view, "pgermishuys/recipe-box");
      await inDocument().get("[data-testid='new-session-folder-clone']").trigger("click");
      await flushPromises();
      await inDocument().get("input[aria-label='Search, or create a folder']").setValue("comet");
      await folderOption("comet").trigger("click");
      await flushPromises();
      finish({ path: "/home/me/src/recipe-box", isGitRepo: true, addedToFleet: false, warning: null });
      await flushPromises();

      expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("comet");
      expect(mocks.refreshRepositories).toHaveBeenCalled();
    });

    it("Clone a repository… clones one of yours into a location, or anywhere", async () => {
      gitHubRepos.value = [
        { id: 1, full_name: "pgermishuys/rocket", name: "rocket" },
        { id: 2, full_name: "pgermishuys/weave-website", name: "weave-website" },
      ];
      mocks.cloneRepository.mockResolvedValue({ path: "/home/me/work/weave-website", isGitRepo: true, addedToFleet: false, warning: null });
      const view = await mountComposer();

      await openFolderMenu(view);
      await folderOption("Clone a repository").trigger("click");
      await flushPromises();

      expect(mocks.refreshGitHubRepos).toHaveBeenCalled();
      // Only repositories that aren't on this computer yet.
      const suggestions = inDocument().findAll(".ns-folder-suggestion").map((button) => button.text());
      expect(suggestions).toEqual(["pgermishuys/weave-website"]);

      await inDocument().findAll(".ns-folder-suggestion")[0]!.trigger("click");
      await inDocument().get("[data-testid='new-session-clone-location']").setValue(`${String.fromCharCode(0)}other`);
      await inDocument().get("[data-testid='new-session-clone-parent']").setValue("/srv");
      expect(inDocument().get("[data-testid='new-session-clone-preview']").text()).toContain("/srv/weave-website");
      expect(inDocument().get("[data-testid='new-session-clone-preview']").text()).toContain("added to your locations");

      await inDocument().get("[data-testid='new-session-clone-location']").setValue("/home/me/work");
      await inDocument().get("[data-testid='new-session-clone-submit']").trigger("click");
      await flushPromises();

      expect(mocks.cloneRepository).toHaveBeenCalledWith("pgermishuys/weave-website", "/home/me/work/weave-website", expect.any(Function), api);
      expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("weave-website");
    });

    it("suggests nothing from GitHub when the account has no repositories, and filters by what is typed", async () => {
      const view = await mountComposer();
      await openFolderMenu(view);
      await folderOption("Clone a repository").trigger("click");
      await flushPromises();
      expect(inDocument().findAll(".ns-folder-suggestion")).toHaveLength(0);
      expect(mocks.refreshGitHubRepos).toHaveBeenCalledTimes(1);

      gitHubRepos.value = [
        { id: 2, full_name: "pgermishuys/weave-website", name: "weave-website" },
        { id: 3, full_name: "pgermishuys/garden-notes", name: "garden-notes" },
      ];
      await flushPromises();
      expect(inDocument().findAll(".ns-folder-suggestion")).toHaveLength(2);

      await inDocument().get("[data-testid='new-session-clone-repository']").setValue("garden");
      expect(inDocument().findAll(".ns-folder-suggestion").map((button) => button.text())).toEqual(["pgermishuys/garden-notes"]);
    });

    it("isn't offered with a GitHub issue attached", async () => {
      useWorkspaceUiStore().setNewSessionInitialSource(createGitHubSessionSourcePreset({
        sourceType: "github-issue",
        owner: "acme",
        repo: "rocket",
        number: 7,
        title: "Login",
        body: null,
        htmlUrl: "https://github.com/acme/rocket/issues/7",
        repoFullName: "acme/rocket",
        suggestedBranch: "fix/issue-7",
      }));
      const view = await mountComposer();

      await openFolderMenu(view);

      expect(inDocument().find("input[aria-label='Search, or create a folder']").exists()).toBe(false);
      const labels = inDocument().findAll("[role='option']").map((option) => option.text());
      expect(labels.some((label) => label.includes("Open or create a folder") || label.includes("Clone a repository"))).toBe(false);
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

  describe("machine", () => {
    const macbook: MachineConnection = {
      id: "m-mac",
      name: "macbook",
      baseUrl: "http://mac.test:2113",
      token: "mac-token",
      os: "macos",
      addedAt: "2026-09-26T00:00:00Z",
    };
    const falcon: MachineConnection = { ...macbook, id: "m-falcon", name: "falcon", baseUrl: "http://falcon.test:2113" };

    beforeEach(() => {
      // Home answers as andromeda; macbook lists its sessions; falcon is away.
      vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
        const url = String(input instanceof Request ? input.url : input);
        if (url.startsWith(falcon.baseUrl)) throw new TypeError("Failed to fetch");
        if (url.endsWith("/api/machine")) return Response.json({ id: "home-id", name: "andromeda", os: "linux" });
        return Response.json([]);
      }));
    });

    afterEach(() => {
      vi.unstubAllGlobals();
    });

    function addMachines(...machines: MachineConnection[]): void {
      localStorage.setItem("weave:machines", JSON.stringify(machines));
    }

    async function openMachineMenu(view: VueWrapper): Promise<void> {
      await view.get("[data-testid='new-session-machine']").trigger("click");
      await flushPromises();
    }

    it("has no machine chip until another machine is added", async () => {
      const view = await mountComposer();

      expect(view.find("[data-testid='new-session-machine']").exists()).toBe(false);
    });

    it("shows the machine first, and lists every machine with whether it can be reached", async () => {
      addMachines(macbook, falcon);
      const view = await mountComposer();
      await flushPromises();

      const chips = view.findAll(".new-session__strip .ns-chip");
      expect(chips[0]!.attributes("data-testid")).toBe("new-session-machine");
      expect(chips[0]!.text()).toContain("andromeda");

      await openMachineMenu(view);
      const home = inDocument().get("[data-testid='new-session-machine-home']");
      expect(home.text()).toContain("Working here · Linux");
      expect(inDocument().get("[data-testid='new-session-machine-m-mac']").attributes("data-disabled")).toBeUndefined();
      const away = inDocument().get("[data-testid='new-session-machine-m-falcon']");
      expect(away.text()).toContain("Unreachable");
      expect(away.attributes("data-disabled")).toBeDefined();
    });

    it("moves the draft to the machine picked, keeping the message and dropping this machine's folder", async () => {
      addMachines(macbook);
      rememberFolder({ kind: "repository", path: rocket.path });
      const view = await mountComposer();
      await type(view, "Fix the sign-in loop");

      await openMachineMenu(view);
      await inDocument().get("[data-testid='new-session-machine-m-mac']").trigger("click");
      await flushPromises();

      const workspaceUi = useWorkspaceUiStore();
      expect(workspaceUi.newSessionMachine).toBe("m-mac");
      expect(workspaceUi.newSessionDraft?.message).toBe("Fix the sign-in loop");
      expect(workspaceUi.newSessionDraft?.folder).toBeNull();
    });

    it("Add a machine opens Settings → Machines", async () => {
      addMachines(macbook);
      const view = await mountComposer();

      await openMachineMenu(view);
      await inDocument().get("[data-testid='new-session-machine-add']").trigger("click");
      await flushPromises();

      expect(useSettingsNav().activeSection.value).toBe("machines");
      expect(mocks.navigate).toHaveBeenCalledWith({ to: "/settings" });
    });

    describe("on another machine", () => {
      async function mountOn(machine: MachineConnection): Promise<VueWrapper> {
        const Page = defineComponent({
          setup() {
            provideMachineTarget(() => targetFor(machine));
            return () => h(NewSessionComposer);
          },
        });
        wrapper = mount(Page, { attachTo: document.body, global: { stubs: { teleport: false } } });
        await flushPromises();
        return wrapper;
      }

      it("remembers its own folders and says where the session starts", async () => {
        addMachines(macbook);
        rememberFolder({ kind: "repository", path: rocket.path });
        localStorage.setItem(newSessionDefaultsKey("m-mac"), JSON.stringify({ lastFolder: { kind: "repository", path: comet.path } }));
        const view = await mountOn(macbook);

        expect(view.get("[data-testid='new-session-folder-chip']").text()).toContain("comet");
        expect(view.get("[data-testid='new-session-machine']").text()).toContain("macbook");
        expect(view.get("[data-testid='new-session-plan-machine']").text()).toBe("On macbook:");
      });

      it("starts the session there, then opens it on that machine", async () => {
        addMachines(macbook);
        localStorage.setItem(newSessionDefaultsKey("m-mac"), JSON.stringify({ lastFolder: { kind: "repository", path: comet.path } }));
        const view = await mountOn(macbook);
        const openOn = vi.spyOn(useMachinesStore(), "openOn").mockImplementation(() => {});

        await type(view, "Fix the sign-in loop");
        await pressEnter(view);

        expect(lastCreateCall()[0]).toBe(comet.path);
        expect(openOn).toHaveBeenCalledWith("m-mac", "/sessions/session-1?instanceId=instance-1");
        expect(mocks.navigate).not.toHaveBeenCalled();
        expect(loadSessionMachines()["session-1"]).toBe("m-mac");
        expect(useWorkspaceUiStore().newSessionDraft).toBeNull();
      });

      it("with every machine live, opens it in place, its row kept out of the live machine's list", async () => {
        addMachines(macbook);
        localStorage.setItem(newSessionDefaultsKey("m-mac"), JSON.stringify({ lastFolder: { kind: "repository", path: comet.path } }));
        const view = await mountOn(macbook);
        const preferences = usePreferencesStore();
        preferences.hasFetched = true;
        preferences.preferences = { ...preferences.preferences, LiveMachines: "true" };
        const openOn = vi.spyOn(useMachinesStore(), "openOn").mockImplementation(() => {});

        await type(view, "Fix the sign-in loop");
        await pressEnter(view);

        expect(openOn).not.toHaveBeenCalled();
        expect(mocks.navigate).toHaveBeenCalledWith(expect.objectContaining({ to: "/sessions/$id", params: { id: "session-1" } }));
        expect(useSessionsStore().sessions.map((item) => item.session.id)).not.toContain("session-1");
        expect(useSessionsStore().sessionById("session-1")).not.toBeNull();
        expect(useMachinesStore().sessionTarget("session-1")).toMatchObject({ key: "m-mac", isLive: false });
      });

      it("sends a harness that isn't set up there to that machine's Settings", async () => {
        addMachines(macbook);
        noHarnessReason.value = "OpenCode isn't installed.";
        const view = await mountOn(macbook);
        const openOn = vi.spyOn(useMachinesStore(), "openOn").mockImplementation(() => {});

        const fix = view.get("[data-testid='new-session-no-harness'] button");
        expect(fix.text()).toBe("Open macbook's Settings");
        await fix.trigger("click");

        expect(openOn).toHaveBeenCalledWith("m-mac", "/settings");
      });
    });
  });
});
