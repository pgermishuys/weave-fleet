import { tmpdir } from "os";
import { join } from "path";
import {
  mkdirSync,
  writeFileSync,
  rmSync,
  existsSync,
} from "fs";
import { randomUUID } from "crypto";
import {
  installSkillFromSource,
  removeInstalledSkill,
} from "@/lib/server/config-manager";

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
  };
});

describe("skills API logic", () => {
  let sourceDir: string;

  beforeEach(() => {
    testConfigDir = join(tmpdir(), `skills-api-test-${randomUUID()}`);
    testSkillsDir = join(testConfigDir, "skills");
    sourceDir = join(tmpdir(), `skills-source-${randomUUID()}`);
    mkdirSync(testConfigDir, { recursive: true });
    mkdirSync(testSkillsDir, { recursive: true });
    mkdirSync(sourceDir, { recursive: true });
  });

  afterEach(() => {
    for (const dir of [testConfigDir, sourceDir]) {
      if (existsSync(dir)) {
        rmSync(dir, { recursive: true, force: true });
      }
    }
  });

  describe("installSkillFromSource", () => {
    it("InstallsFromContent", async () => {
      const content = `---
name: content-skill
description: Installed from content
---
# Content Skill`;

      const result = await installSkillFromSource({ content });

      expect(result.name).toBe("content-skill");
      expect(result.description).toBe("Installed from content");
      expect(existsSync(join(testSkillsDir, "content-skill", "SKILL.md"))).toBe(
        true
      );
    });

    it("ErrorsWithoutUrlOrContent", async () => {
      await expect(installSkillFromSource({})).rejects.toThrow(
        "Either 'url' or 'content' must be provided"
      );
    });

    it("InstallsWithAgentAssignments", async () => {
      const content = `---
name: agent-assigned
description: With agents
---
# Content`;

      const result = await installSkillFromSource({
        content,
        agents: ["tapestry"],
      });

      expect(result.name).toBe("agent-assigned");
    });
  });

  describe("removeInstalledSkill", () => {
    it("RemovesExistingSkill", () => {
      const skillDir = join(testSkillsDir, "removable");
      mkdirSync(skillDir);
      writeFileSync(
        join(skillDir, "SKILL.md"),
        `---\nname: removable\ndescription: To remove\n---\n# Content`
      );

      removeInstalledSkill("removable");
      expect(existsSync(skillDir)).toBe(false);
    });

    it("ErrorsForMissingSkill", () => {
      expect(() => removeInstalledSkill("ghost")).toThrow("not installed");
    });
  });
});
