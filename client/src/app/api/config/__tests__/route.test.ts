import { join } from "path";
import {
  mkdirSync,
  rmSync,
  existsSync,
  readFileSync,
} from "fs";

import {
  getUserConfig,
  getProjectConfig,
  getMergedConfig,
  updateUserConfig,
  listInstalledSkills,
  getConfigPaths,
} from "@/lib/server/config-manager";
import { getConnectedProviders } from "@/lib/server/auth-store";
import { BUNDLED_PROVIDERS } from "@/lib/provider-registry";
import { createSecureTempDir, writeTempFile } from "@/lib/server/__tests__/test-temp-utils";

// We need to mock the config-paths module to use temp directories
// so we don't modify real user config during tests

let testConfigDir: string;
let testSkillsDir: string;

vi.mock("@/cli/config-paths", () => {
  return {
    getUserConfigDir: () => testConfigDir,
    getUserWeaveConfigPath: () => join(testConfigDir, "weave-opencode.jsonc"),
    getSkillsDir: () => testSkillsDir,
    getProjectConfigDir: (dir: string) => join(dir, ".opencode"),
    getProjectWeaveConfigPath: (dir: string) =>
      join(dir, ".opencode", "weave-opencode.jsonc"),
    getDataDir: () => join(testConfigDir, "data"),
    getAuthJsonPath: () => join(testConfigDir, "data", "auth.json"),
  };
});

describe("config-manager", () => {
  let testProjectDir: string;

  beforeEach(() => {
    testConfigDir = createSecureTempDir("config-mgr-test-");
    testSkillsDir = join(testConfigDir, "skills");
    testProjectDir = createSecureTempDir("project-test-");
    mkdirSync(testSkillsDir, { recursive: true });
  });

  afterEach(() => {
    for (const dir of [testConfigDir, testProjectDir]) {
      if (existsSync(dir)) {
        rmSync(dir, { recursive: true, force: true });
      }
    }
  });

  describe("getUserConfig", () => {
    it("ReturnsNullWhenNoConfigExists", () => {
      const result = getUserConfig();
      expect(result).toBeNull();
    });

    it("ReturnsConfigWhenFileExists", () => {
      writeTempFile(
        testConfigDir,
        "weave-opencode.jsonc",
        JSON.stringify({
          agents: { tapestry: { skills: ["skill-a"] } },
        })
      );

      const result = getUserConfig();
      expect(result).not.toBeNull();
      expect(result!.agents!.tapestry.skills).toEqual(["skill-a"]);
    });
  });

  describe("updateUserConfig", () => {
    it("WritesConfigFile", () => {
      const config = {
        agents: { shuttle: { skills: ["new-skill"] } },
      };
      updateUserConfig(config);

      const configPath = join(testConfigDir, "weave-opencode.jsonc");
      expect(existsSync(configPath)).toBe(true);

      const content = JSON.parse(readFileSync(configPath, "utf-8"));
      expect(content.agents.shuttle.skills).toEqual(["new-skill"]);
    });
  });

  describe("getProjectConfig", () => {
    it("ReturnsNullWhenNoProjectConfig", () => {
      const result = getProjectConfig(testProjectDir);
      expect(result).toBeNull();
    });

    it("ReturnsProjectConfigWhenExists", () => {
      writeTempFile(
        testProjectDir,
        join(".opencode", "weave-opencode.jsonc"),
        JSON.stringify({
          agents: { weft: { skills: ["reviewing-code"] } },
        })
      );

      const result = getProjectConfig(testProjectDir);
      expect(result).not.toBeNull();
      expect(result!.agents!.weft.skills).toEqual(["reviewing-code"]);
    });
  });

  describe("getMergedConfig", () => {
    it("MergesUserAndProjectConfigs", () => {
      // User config
      writeTempFile(
        testConfigDir,
        "weave-opencode.jsonc",
        JSON.stringify({
          agents: {
            tapestry: { skills: ["user-skill"] },
            shuttle: { skills: ["shared-skill"] },
          },
        })
      );

      // Project config
      writeTempFile(
        testProjectDir,
        join(".opencode", "weave-opencode.jsonc"),
        JSON.stringify({
          agents: {
            shuttle: { skills: ["project-skill"] },
            weft: { skills: ["project-only"] },
          },
        })
      );

      const result = getMergedConfig(testProjectDir);

      expect(result.agents!.tapestry.skills).toEqual(["user-skill"]);
      // Project overrides user for shuttle
      expect(result.agents!.shuttle.skills).toEqual(["project-skill"]);
      expect(result.agents!.weft.skills).toEqual(["project-only"]);
    });

    it("ReturnsUserConfigWhenNoProjectConfig", () => {
      writeTempFile(
        testConfigDir,
        "weave-opencode.jsonc",
        JSON.stringify({
          agents: { tapestry: { skills: ["only-user"] } },
        })
      );

      const result = getMergedConfig(testProjectDir);
      expect(result.agents!.tapestry.skills).toEqual(["only-user"]);
    });
  });

  describe("listInstalledSkills", () => {
    it("ListsSkillsFromSkillsDir", () => {
      const skillDir = join(testSkillsDir, "test-skill");
      mkdirSync(skillDir);
      writeTempFile(
        skillDir,
        "SKILL.md",
        `---\nname: test-skill\ndescription: A test\n---\n# Content`
      );

      const result = listInstalledSkills();
      expect(result).toHaveLength(1);
      expect(result[0].name).toBe("test-skill");
    });
  });

  describe("getConfigPaths", () => {
    it("ReturnsExpectedPaths", () => {
      const paths = getConfigPaths();
      expect(paths.userConfig).toContain("weave-opencode.jsonc");
      expect(paths.skillsDir).toContain("skills");
    });
  });

  describe("connectedProviders via auth-store", () => {
    it("ReturnsEmptyArrayWhenAuthJsonDoesNotExist", () => {
      const result = getConnectedProviders();
      expect(result).toEqual([]);
    });

    it("ReturnsConnectedProvidersFromAuthJson", () => {
      writeTempFile(
        testConfigDir,
        join("data", "auth.json"),
        JSON.stringify({
          anthropic: { type: "api", token: "sk-ant-xxx" },
          "github-copilot": { type: "oauth", token: "ghu_xxx" },
        })
      );

      const result = getConnectedProviders();
      expect(result).toHaveLength(2);
      expect(result[0]).toEqual({ id: "anthropic", authType: "api" });
      expect(result[1]).toEqual({ id: "github-copilot", authType: "oauth" });
    });

    it("BundledProvidersRegistryHasExpectedProviders", () => {
      const ids = BUNDLED_PROVIDERS.map((p) => p.id);
      expect(ids).toContain("anthropic");
      expect(ids).toContain("openai");
      expect(ids).toContain("google");
      expect(ids).toContain("github-copilot");
    });
  });

  describe("config with model field", () => {
    it("WritesAndReadsConfigWithModelField", () => {
      const config = {
        agents: {
          tapestry: { skills: ["skill-a"], model: "anthropic/claude-sonnet-4-5" },
          shuttle: { skills: ["skill-b"] },
        },
      };
      updateUserConfig(config);

      const readBack = getUserConfig();
      expect(readBack).not.toBeNull();
      expect(readBack!.agents!.tapestry.model).toBe("anthropic/claude-sonnet-4-5");
      expect(readBack!.agents!.shuttle.model).toBeUndefined();
    });

    it("MergesModelFieldCorrectly", () => {
      // User config with model
      writeTempFile(
        testConfigDir,
        "weave-opencode.jsonc",
        JSON.stringify({
          agents: {
            tapestry: { skills: ["skill-a"], model: "anthropic/claude-sonnet-4-5" },
          },
        })
      );

      // Project config overrides model
      writeTempFile(
        testProjectDir,
        join(".opencode", "weave-opencode.jsonc"),
        JSON.stringify({
          agents: {
            tapestry: { model: "openai/gpt-4.1" },
          },
        })
      );

      const result = getMergedConfig(testProjectDir);
      expect(result.agents!.tapestry.model).toBe("openai/gpt-4.1");
    });
  });
});
