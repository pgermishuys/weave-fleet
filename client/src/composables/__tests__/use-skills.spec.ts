import { beforeEach, describe, expect, it, vi } from "vitest";
import { useSkills } from "@/composables/use-skills";
import type { components } from "@/api/client";
import { flushAll, mountComposable } from "./test-utils";

type SkillManifestEntry = components["schemas"]["SkillManifestEntry"];
type UpdateCheckResponse = components["schemas"]["UpdateCheckResponse"];
type UpdateSkillResponse = components["schemas"]["UpdateSkillResponse"];
type InstallSkillResponse = components["schemas"]["InstallSkillResponse"];

const { apiMock } = vi.hoisted(() => ({
  apiMock: {
    GET: vi.fn(),
    POST: vi.fn(),
    DELETE: vi.fn(),
  },
}));

vi.mock("@/api/client", () => ({
  api: apiMock,
}));

function createSkillEntry(
  name: string,
  overrides: Partial<SkillManifestEntry> = {},
): SkillManifestEntry {
  return {
    name,
    source: 1,
    repoUrl: `https://github.com/example/${name}`,
    ref: "main",
    localPath: null,
    targetHarnesses: ["opencode"],
    installedAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
    ...overrides,
  };
}

describe("useSkills", () => {
  beforeEach(() => {
    apiMock.GET.mockReset();
    apiMock.POST.mockReset();
    apiMock.DELETE.mockReset();
  });

  describe("fetchSkills", () => {
    it("fetches and transforms skills from the manifest endpoint", async () => {
      const manifestResponse = {
        skills: [
          createSkillEntry("skill-one"),
          createSkillEntry("skill-two", {
            source: 2,
            localPath: "/path/to/skill",
            repoUrl: null,
          }),
        ],
      };

      apiMock.GET.mockResolvedValue({
        data: manifestResponse,
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkills());

      expect(apiMock.GET).toHaveBeenCalledWith("/api/skills/manifest");
      expect(result.skills.value).toHaveLength(2);
      expect(result.skills.value[0]).toMatchObject({
        name: "skill-one",
        source: "GitHub",
        repoUrl: "https://github.com/example/skill-one",
        ref: "main",
        targetHarnesses: ["opencode"],
        // Backward compatibility fields
        description: "https://github.com/example/skill-one",
        path: "https://github.com/example/skill-one",
      });
      expect(result.skills.value[1]).toMatchObject({
        name: "skill-two",
        source: "Local",
        localPath: "/path/to/skill",
        path: "/path/to/skill",
      });
      expect(result.isLoading.value).toBe(false);
      expect(result.error.value).toBeUndefined();
    });

    it("handles empty skills list", async () => {
      apiMock.GET.mockResolvedValue({
        data: { skills: [] },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkills());

      expect(result.skills.value).toEqual([]);
      expect(result.isLoading.value).toBe(false);
      expect(result.error.value).toBeUndefined();
    });

    it("handles API errors", async () => {
      apiMock.GET.mockResolvedValue({
        data: undefined,
        error: "Network error",
      });

      const { result } = await mountComposable(() => useSkills());

      expect(result.skills.value).toEqual([]);
      expect(result.isLoading.value).toBe(false);
      expect(result.error.value).toBe("Network error");
    });

    it("handles missing data response", async () => {
      apiMock.GET.mockResolvedValue({
        data: null,
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkills());

      expect(result.error.value).toBe("No data returned");
      expect(result.isLoading.value).toBe(false);
    });

    it("can be called manually to refresh", async () => {
      apiMock.GET.mockResolvedValue({
        data: { skills: [createSkillEntry("skill-one")] },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkills());

      expect(apiMock.GET).toHaveBeenCalledTimes(1);

      apiMock.GET.mockResolvedValue({
        data: {
          skills: [createSkillEntry("skill-one"), createSkillEntry("skill-two")],
        },
        error: undefined,
      });

      await result.fetchSkills();
      await flushAll();

      expect(apiMock.GET).toHaveBeenCalledTimes(2);
      expect(result.skills.value).toHaveLength(2);
    });
  });

  describe("checkUpdate", () => {
    it("checks for updates and updates skill state", async () => {
      apiMock.GET.mockResolvedValueOnce({
        data: { skills: [createSkillEntry("test-skill")] },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkills());

      const updateCheckResponse: UpdateCheckResponse = {
        name: "test-skill",
        updateAvailable: true,
        remoteRef: "v2.0.0",
        localRef: "main",
        message: "Update available",
      };

      apiMock.GET.mockResolvedValueOnce({
        data: updateCheckResponse,
        error: undefined,
      });

      const response = await result.checkUpdate("test-skill");

      expect(apiMock.GET).toHaveBeenCalledWith("/api/skills/{name}/update-check", {
        params: { path: { name: "test-skill" } },
      });
      expect(response).toEqual(updateCheckResponse);
      expect(result.skills.value[0]?.updateAvailable).toBe(true);
      expect(result.skills.value[0]?.updateCheckError).toBeUndefined();
      expect(result.error.value).toBeUndefined();
    });

    it("handles update check errors and updates skill state", async () => {
      apiMock.GET.mockResolvedValueOnce({
        data: { skills: [createSkillEntry("test-skill")] },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkills());

      apiMock.GET.mockResolvedValueOnce({
        data: undefined,
        error: "Failed to check updates",
      });

      const response = await result.checkUpdate("test-skill");

      expect(response).toBeUndefined();
      expect(result.skills.value[0]?.updateCheckError).toBe("Failed to check updates");
      expect(result.error.value).toBe("Failed to check updates");
    });

    it("handles non-existent skill gracefully", async () => {
      apiMock.GET.mockResolvedValueOnce({
        data: { skills: [createSkillEntry("other-skill")] },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkills());

      apiMock.GET.mockResolvedValueOnce({
        data: { updateAvailable: false },
        error: undefined,
      });

      await result.checkUpdate("non-existent");

      // Should not crash, just not update any skill
      expect(result.skills.value).toHaveLength(1);
      expect(result.skills.value[0]?.name).toBe("other-skill");
    });
  });

  describe("updateSkill", () => {
    it("updates a skill and refreshes the list", async () => {
      apiMock.GET.mockResolvedValueOnce({
        data: { skills: [createSkillEntry("test-skill")] },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkills());

      const updateResponse: UpdateSkillResponse = {
        name: "test-skill",
        updatedAt: "2026-01-02T00:00:00Z",
        syncResults: [],
      };

      apiMock.POST.mockResolvedValueOnce({
        data: updateResponse,
        error: undefined,
      });

      apiMock.GET.mockResolvedValueOnce({
        data: {
          skills: [
            createSkillEntry("test-skill", {
              ref: "v2.0.0",
              updatedAt: "2026-01-02T00:00:00Z",
            }),
          ],
        },
        error: undefined,
      });

      const response = await result.updateSkill("test-skill");

      expect(apiMock.POST).toHaveBeenCalledWith("/api/skills/{name}/update", {
        params: { path: { name: "test-skill" } },
      });
      expect(response).toEqual(updateResponse);
      expect(apiMock.GET).toHaveBeenCalledTimes(2); // Initial + refresh
      expect(result.skills.value[0]?.ref).toBe("v2.0.0");
      expect(result.error.value).toBeUndefined();
    });

    it("handles update errors", async () => {
      apiMock.GET.mockResolvedValueOnce({
        data: { skills: [createSkillEntry("test-skill")] },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkills());

      apiMock.POST.mockResolvedValueOnce({
        data: undefined,
        error: "Update failed",
      });

      await expect(result.updateSkill("test-skill")).rejects.toThrow("Update failed");
      expect(result.error.value).toBe("Update failed");
    });

    it("handles missing data response", async () => {
      apiMock.GET.mockResolvedValueOnce({
        data: { skills: [createSkillEntry("test-skill")] },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkills());

      apiMock.POST.mockResolvedValueOnce({
        data: null,
        error: undefined,
      });

      await expect(result.updateSkill("test-skill")).rejects.toThrow("No data returned");
    });
  });

  describe("removeSkill", () => {
    it("removes a skill and refreshes the list", async () => {
      apiMock.GET.mockResolvedValueOnce({
        data: {
          skills: [createSkillEntry("skill-one"), createSkillEntry("skill-two")],
        },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkills());

      expect(result.skills.value).toHaveLength(2);

      apiMock.DELETE.mockResolvedValueOnce({
        data: undefined,
        error: undefined,
      });

      apiMock.GET.mockResolvedValueOnce({
        data: { skills: [createSkillEntry("skill-two")] },
        error: undefined,
      });

      await result.removeSkill("skill-one");

      expect(apiMock.DELETE).toHaveBeenCalledWith("/api/skills/{name}", {
        params: { path: { name: "skill-one" } },
      });
      expect(apiMock.GET).toHaveBeenCalledTimes(2); // Initial + refresh
      expect(result.skills.value).toHaveLength(1);
      expect(result.skills.value[0]?.name).toBe("skill-two");
      expect(result.error.value).toBeUndefined();
    });

    it("handles removal errors", async () => {
      apiMock.GET.mockResolvedValueOnce({
        data: { skills: [createSkillEntry("test-skill")] },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkills());

      apiMock.DELETE.mockResolvedValueOnce({
        data: undefined,
        error: "Removal failed",
      });

      await expect(result.removeSkill("test-skill")).rejects.toThrow("Removal failed");
      expect(result.error.value).toBe("Removal failed");
    });
  });

  describe("installSkill (legacy)", () => {
    it("installs a GitHub skill and refreshes the list", async () => {
      apiMock.GET.mockResolvedValueOnce({
        data: { skills: [] },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkills());

      const installResponse: InstallSkillResponse = {
        name: "new-skill",
        syncResults: [],
      };

      apiMock.POST.mockResolvedValueOnce({
        data: installResponse,
        error: undefined,
      });

      apiMock.GET.mockResolvedValueOnce({
        data: { skills: [createSkillEntry("new-skill")] },
        error: undefined,
      });

      const response = await result.installSkill({
        url: "https://github.com/example/new-skill",
      });

      expect(apiMock.POST).toHaveBeenCalledWith("/api/skills/install", {
        body: {
          name: "new-skill",
          source: 1,
          repoUrl: "https://github.com/example/new-skill.git",
          ref: null,
          localPath: null,
          targetHarnesses: null,
          subPath: null,
        },
      });
      expect(response).toEqual(installResponse);
      expect(result.skills.value).toHaveLength(1);
      expect(result.skills.value[0]?.name).toBe("new-skill");
    });

    it("installs a local skill", async () => {
      apiMock.GET.mockResolvedValueOnce({
        data: { skills: [] },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkills());

      apiMock.POST.mockResolvedValueOnce({
        data: { name: "local-skill", syncResults: [] },
        error: undefined,
      });

      apiMock.GET.mockResolvedValueOnce({
        data: {
          skills: [
            createSkillEntry("local-skill", {
              source: 2,
              localPath: "/path/to/local-skill",
              repoUrl: null,
            }),
          ],
        },
        error: undefined,
      });

      await result.installSkill({ url: "/path/to/local-skill" });

      expect(apiMock.POST).toHaveBeenCalledWith("/api/skills/install", {
        body: {
          name: "local-skill",
          source: 2,
          repoUrl: null,
          ref: null,
          localPath: "/path/to/local-skill",
          targetHarnesses: null,
          subPath: null,
        },
      });
    });

    it("handles missing URL", async () => {
      apiMock.GET.mockResolvedValueOnce({
        data: { skills: [] },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkills());

      await expect(result.installSkill({ url: "" })).rejects.toThrow("URL is required");
      expect(result.error.value).toBe("URL is required");
    });

    it("handles installation errors", async () => {
      apiMock.GET.mockResolvedValueOnce({
        data: { skills: [] },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkills());

      apiMock.POST.mockResolvedValueOnce({
        data: undefined,
        error: "Installation failed",
      });

      await expect(
        result.installSkill({ url: "https://github.com/example/skill" }),
      ).rejects.toThrow("Installation failed");
      expect(result.error.value).toBe("Installation failed");
    });
  });

  describe("reactive state", () => {
    it("exposes readonly reactive state", async () => {
      apiMock.GET.mockResolvedValue({
        data: { skills: [createSkillEntry("test-skill")] },
        error: undefined,
      });

      const { result } = await mountComposable(() => useSkills());

      // Verify readonly refs
      expect(result.skills.value).toBeDefined();
      expect(result.isLoading.value).toBe(false);
      expect(result.error.value).toBeUndefined();

      // Verify they are readonly - Vue warns but doesn't throw in production
      // TypeScript enforces this at compile time
      const originalValue = result.skills.value;
      // @ts-expect-error - testing readonly enforcement
      result.skills.value = [];
      // Value should not have changed
      expect(result.skills.value).toBe(originalValue);
    });

    it("updates loading state during fetch", async () => {
      let resolvePromise: (value: unknown) => void;
      const promise = new Promise((resolve) => {
        resolvePromise = resolve;
      });

      apiMock.GET.mockReturnValue(promise);

      const { result } = await mountComposable(() => useSkills());

      // Should be loading initially
      expect(result.isLoading.value).toBe(true);

      resolvePromise!({
        data: { skills: [createSkillEntry("test-skill")] },
        error: undefined,
      });

      await flushAll();

      expect(result.isLoading.value).toBe(false);
    });
  });
});
