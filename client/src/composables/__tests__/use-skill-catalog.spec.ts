import { beforeEach, describe, expect, it, vi } from "vitest";
import { useSkillCatalog } from "@/composables/use-skill-catalog";
import type { components } from "@/api/client";
import { flushAll, mountComposable } from "./test-utils";

type SkillCatalogEntry = components["schemas"]["SkillCatalogEntry"];
type SkillCatalogResponse = components["schemas"]["SkillCatalogResponse"];
type InstallSkillRequest = components["schemas"]["InstallSkillRequest"];
type InstallSkillResponse = components["schemas"]["InstallSkillResponse"];

const { apiMock } = vi.hoisted(() => ({
  apiMock: {
    GET: vi.fn(),
    POST: vi.fn(),
  },
}));

vi.mock("@/api/client", () => ({
  api: apiMock,
}));

function createCatalogEntry(
  name: string,
  overrides: Partial<SkillCatalogEntry> = {},
): SkillCatalogEntry {
  return {
    name,
    displayName: `${name} Display`,
    description: `Description for ${name}`,
    source: "GitHub",
    repoUrl: `https://github.com/example/${name}`,
    ref: "main",
    localPath: null,
    targetHarnesses: ["opencode"],
    author: "Example Author",
    version: "1.0.0",
    tags: ["tag1", "tag2"],
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
    ...overrides,
  };
}

describe("useSkillCatalog", () => {
  beforeEach(() => {
    apiMock.GET.mockReset();
    apiMock.POST.mockReset();
  });

  describe("fetchCatalog", () => {
    it("fetches and transforms catalog entries", async () => {
      const catalogResponse: SkillCatalogResponse = {
        entries: [
          createCatalogEntry("skill-one"),
          createCatalogEntry("skill-two", {
            source: "Local",
            localPath: "/path/to/skill",
            repoUrl: null,
          }),
        ],
        isStale: false,
        cachedAt: "2026-01-01T12:00:00Z",
      };

      apiMock.GET.mockResolvedValue({
        data: catalogResponse,
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkillCatalog());

      expect(apiMock.GET).toHaveBeenCalledWith("/api/skills/catalog");
      expect(result.catalog.value).toHaveLength(2);
      expect(result.catalog.value[0]).toMatchObject({
        name: "skill-one",
        displayName: "skill-one Display",
        description: "Description for skill-one",
        source: "GitHub",
        repoUrl: "https://github.com/example/skill-one",
        ref: "main",
        targetHarnesses: ["opencode"],
        author: "Example Author",
        version: "1.0.0",
        tags: ["tag1", "tag2"],
      });
      expect(result.catalog.value[1]).toMatchObject({
        name: "skill-two",
        source: "Local",
        localPath: "/path/to/skill",
      });
      expect(result.isStale.value).toBe(false);
      expect(result.cachedAt.value).toBe("2026-01-01T12:00:00Z");
      expect(result.isLoading.value).toBe(false);
      expect(result.error.value).toBeUndefined();
    });

    it("handles empty catalog", async () => {
      apiMock.GET.mockResolvedValue({
        data: {
          entries: [],
          isStale: false,
          cachedAt: null,
        },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkillCatalog());

      expect(result.catalog.value).toEqual([]);
      expect(result.isStale.value).toBe(false);
      expect(result.cachedAt.value).toBeNull();
      expect(result.isLoading.value).toBe(false);
      expect(result.error.value).toBeUndefined();
    });

    it("handles stale catalog", async () => {
      const catalogResponse: SkillCatalogResponse = {
        entries: [createCatalogEntry("skill-one")],
        isStale: true,
        cachedAt: "2026-01-01T00:00:00Z",
      };

      apiMock.GET.mockResolvedValue({
        data: catalogResponse,
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkillCatalog());

      expect(result.isStale.value).toBe(true);
      expect(result.cachedAt.value).toBe("2026-01-01T00:00:00Z");
    });

    it("handles API errors", async () => {
      apiMock.GET.mockResolvedValue({
        data: undefined,
        error: "Network error",
      });

      const { result } = await mountComposable(() => useSkillCatalog());

      expect(result.catalog.value).toEqual([]);
      expect(result.isLoading.value).toBe(false);
      expect(result.error.value).toBe("Network error");
    });

    it("handles missing data response", async () => {
      apiMock.GET.mockResolvedValue({
        data: null,
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkillCatalog());

      expect(result.error.value).toBe("No data returned");
      expect(result.isLoading.value).toBe(false);
    });

    it("can be called manually to refresh", async () => {
      apiMock.GET.mockResolvedValue({
        data: {
          entries: [createCatalogEntry("skill-one")],
          isStale: false,
          cachedAt: "2026-01-01T00:00:00Z",
        },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkillCatalog());

      expect(apiMock.GET).toHaveBeenCalledTimes(1);

      apiMock.GET.mockResolvedValue({
        data: {
          entries: [createCatalogEntry("skill-one"), createCatalogEntry("skill-two")],
          isStale: false,
          cachedAt: "2026-01-01T12:00:00Z",
        },
        error: undefined,
      });

      await result.fetchCatalog();
      await flushAll();

      expect(apiMock.GET).toHaveBeenCalledTimes(2);
      expect(result.catalog.value).toHaveLength(2);
      expect(result.cachedAt.value).toBe("2026-01-01T12:00:00Z");
    });

    it("handles missing optional fields gracefully", async () => {
      const catalogResponse: SkillCatalogResponse = {
        entries: [
          {
            name: "minimal-skill",
            displayName: null,
            description: null,
            source: "GitHub",
            repoUrl: "https://github.com/example/minimal",
            ref: null,
            localPath: null,
            targetHarnesses: [],
            author: null,
            version: null,
            tags: [],
            createdAt: null,
            updatedAt: null,
          },
        ],
        isStale: null,
        cachedAt: null,
      };

      apiMock.GET.mockResolvedValue({
        data: catalogResponse,
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkillCatalog());

      expect(result.catalog.value).toHaveLength(1);
      expect(result.catalog.value[0]).toMatchObject({
        name: "minimal-skill",
        displayName: null,
        description: null,
        source: "GitHub",
        repoUrl: "https://github.com/example/minimal",
        ref: null,
        targetHarnesses: [],
        tags: [],
      });
      expect(result.isStale.value).toBe(false);
      expect(result.cachedAt.value).toBeNull();
    });
  });

  describe("installSkill", () => {
    it("installs a skill from the catalog", async () => {
      apiMock.GET.mockResolvedValue({
        data: {
          entries: [createCatalogEntry("test-skill")],
          isStale: false,
          cachedAt: "2026-01-01T00:00:00Z",
        },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkillCatalog());

      const installRequest: InstallSkillRequest = {
        name: "test-skill",
        source: "GitHub",
        repoUrl: "https://github.com/example/test-skill",
        ref: "main",
        localPath: null,
      };

      const installResponse: InstallSkillResponse = {
        success: true,
        message: "Skill installed successfully",
        skillName: "test-skill",
      };

      apiMock.POST.mockResolvedValue({
        data: installResponse,
        error: undefined,
      });

      const response = await result.installSkill(installRequest);

      expect(apiMock.POST).toHaveBeenCalledWith("/api/skills/install", {
        body: installRequest,
      });
      expect(response).toEqual(installResponse);
      expect(result.error.value).toBeUndefined();
    });

    it("installs a local skill", async () => {
      apiMock.GET.mockResolvedValue({
        data: {
          entries: [],
          isStale: false,
          cachedAt: null,
        },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkillCatalog());

      const installRequest: InstallSkillRequest = {
        name: "local-skill",
        source: "Local",
        repoUrl: null,
        ref: null,
        localPath: "/path/to/local-skill",
      };

      const installResponse: InstallSkillResponse = {
        success: true,
        message: "Local skill installed",
        skillName: "local-skill",
      };

      apiMock.POST.mockResolvedValue({
        data: installResponse,
        error: undefined,
      });

      const response = await result.installSkill(installRequest);

      expect(apiMock.POST).toHaveBeenCalledWith("/api/skills/install", {
        body: installRequest,
      });
      expect(response).toEqual(installResponse);
    });

    it("handles installation errors", async () => {
      apiMock.GET.mockResolvedValue({
        data: {
          entries: [],
          isStale: false,
          cachedAt: null,
        },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkillCatalog());

      const installRequest: InstallSkillRequest = {
        name: "failing-skill",
        source: "GitHub",
        repoUrl: "https://github.com/example/failing",
        ref: null,
        localPath: null,
      };

      apiMock.POST.mockResolvedValue({
        data: undefined,
        error: "Installation failed: skill not found",
      });

      await expect(result.installSkill(installRequest)).rejects.toThrow(
        "Installation failed: skill not found",
      );
      expect(result.error.value).toBe("Installation failed: skill not found");
    });

    it("handles missing data response", async () => {
      apiMock.GET.mockResolvedValue({
        data: {
          entries: [],
          isStale: false,
          cachedAt: null,
        },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkillCatalog());

      const installRequest: InstallSkillRequest = {
        name: "test-skill",
        source: "GitHub",
        repoUrl: "https://github.com/example/test",
        ref: null,
        localPath: null,
      };

      apiMock.POST.mockResolvedValue({
        data: null,
        error: undefined,
      });

      await expect(result.installSkill(installRequest)).rejects.toThrow("No data returned");
      expect(result.error.value).toBe("No data returned");
    });

    it("installs a bundled skill", async () => {
      apiMock.GET.mockResolvedValue({
        data: {
          entries: [
            createCatalogEntry("bundled-skill", {
              source: "Bundled",
              repoUrl: null,
              localPath: null,
            }),
          ],
          isStale: false,
          cachedAt: null,
        },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkillCatalog());

      const installRequest: InstallSkillRequest = {
        name: "bundled-skill",
        source: "Bundled",
        repoUrl: null,
        ref: null,
        localPath: null,
      };

      const installResponse: InstallSkillResponse = {
        success: true,
        message: "Bundled skill enabled",
        skillName: "bundled-skill",
      };

      apiMock.POST.mockResolvedValue({
        data: installResponse,
        error: undefined,
      });

      const response = await result.installSkill(installRequest);

      expect(response).toEqual(installResponse);
    });
  });

  describe("reactive state", () => {
    it("exposes readonly reactive state", async () => {
      apiMock.GET.mockResolvedValue({
        data: {
          entries: [createCatalogEntry("test-skill")],
          isStale: false,
          cachedAt: "2026-01-01T00:00:00Z",
        },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkillCatalog());

      // Verify readonly refs
      expect(result.catalog.value).toBeDefined();
      expect(result.isStale.value).toBe(false);
      expect(result.cachedAt.value).toBe("2026-01-01T00:00:00Z");
      expect(result.isLoading.value).toBe(false);
      expect(result.error.value).toBeUndefined();

      // Verify they are readonly - Vue warns but doesn't throw in production
      // TypeScript enforces this at compile time
      const originalValue = result.catalog.value;
      // @ts-expect-error - testing readonly enforcement
      result.catalog.value = [];
      // Value should not have changed
      expect(result.catalog.value).toBe(originalValue);
    });

    it("updates loading state during fetch", async () => {
      let resolvePromise: (value: unknown) => void;
      const promise = new Promise((resolve) => {
        resolvePromise = resolve;
      });

      apiMock.GET.mockReturnValue(promise);

      const { result } = await mountComposable(() => useSkillCatalog());

      // Should be loading initially
      expect(result.isLoading.value).toBe(true);

      resolvePromise!({
        data: {
          entries: [createCatalogEntry("test-skill")],
          isStale: false,
          cachedAt: "2026-01-01T00:00:00Z",
        },
        error: undefined,
      });

      await flushAll();

      expect(result.isLoading.value).toBe(false);
    });

    it("clears error on successful operations", async () => {
      // First call fails
      apiMock.GET.mockResolvedValueOnce({
        data: undefined,
        error: "Network error",
      });

      const { result } = await mountComposable(() => useSkillCatalog());

      expect(result.error.value).toBe("Network error");

      // Second call succeeds
      apiMock.GET.mockResolvedValueOnce({
        data: {
          entries: [createCatalogEntry("test-skill")],
          isStale: false,
          cachedAt: "2026-01-01T00:00:00Z",
        },
        error: undefined,
      });

      await result.fetchCatalog();
      await flushAll();

      expect(result.error.value).toBeUndefined();
      expect(result.catalog.value).toHaveLength(1);
    });
  });

  describe("integration scenarios", () => {
    it("handles catalog fetch followed by skill installation", async () => {
      const catalogResponse: SkillCatalogResponse = {
        entries: [
          createCatalogEntry("available-skill"),
          createCatalogEntry("another-skill"),
        ],
        isStale: false,
        cachedAt: "2026-01-01T00:00:00Z",
      };

      apiMock.GET.mockResolvedValue({
        data: catalogResponse,
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkillCatalog());

      expect(result.catalog.value).toHaveLength(2);

      const installRequest: InstallSkillRequest = {
        name: "available-skill",
        source: "GitHub",
        repoUrl: "https://github.com/example/available-skill",
        ref: "main",
        localPath: null,
      };

      apiMock.POST.mockResolvedValue({
        data: {
          success: true,
          message: "Installed",
          skillName: "available-skill",
        },
        error: undefined,
      });

      const installResponse = await result.installSkill(installRequest);

      expect(installResponse?.success).toBe(true);
      expect(installResponse?.skillName).toBe("available-skill");
    });

    it("handles multiple catalog refreshes", async () => {
      apiMock.GET.mockResolvedValueOnce({
        data: {
          entries: [createCatalogEntry("skill-v1")],
          isStale: false,
          cachedAt: "2026-01-01T00:00:00Z",
        },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkillCatalog());

      expect(result.catalog.value).toHaveLength(1);
      expect(result.cachedAt.value).toBe("2026-01-01T00:00:00Z");

      apiMock.GET.mockResolvedValueOnce({
        data: {
          entries: [createCatalogEntry("skill-v1"), createCatalogEntry("skill-v2")],
          isStale: false,
          cachedAt: "2026-01-01T12:00:00Z",
        },
        error: undefined,
      });

      await result.fetchCatalog();
      await flushAll();

      expect(result.catalog.value).toHaveLength(2);
      expect(result.cachedAt.value).toBe("2026-01-01T12:00:00Z");

      apiMock.GET.mockResolvedValueOnce({
        data: {
          entries: [
            createCatalogEntry("skill-v1"),
            createCatalogEntry("skill-v2"),
            createCatalogEntry("skill-v3"),
          ],
          isStale: true,
          cachedAt: "2026-01-02T00:00:00Z",
        },
        error: undefined,
      });

      await result.fetchCatalog();
      await flushAll();

      expect(result.catalog.value).toHaveLength(3);
      expect(result.isStale.value).toBe(true);
      expect(result.cachedAt.value).toBe("2026-01-02T00:00:00Z");
    });
  });
});
