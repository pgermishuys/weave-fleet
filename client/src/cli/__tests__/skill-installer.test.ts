import { tmpdir } from "os";
import { join } from "path";
import {
  mkdirSync,
  writeFileSync,
  rmSync,
  existsSync,
  readFileSync,
} from "fs";
import { randomUUID } from "crypto";
import { installSkill, removeSkill, resolveSource } from "../skill-installer";
import { parseJsonc } from "../skill-catalog";

describe("resolveSource", () => {
  it("ResolvesRawUrl", () => {
    const result = resolveSource("https://example.com/SKILL.md");
    expect(result.type).toBe("url");
    expect(result.resolved).toBe("https://example.com/SKILL.md");
  });

  it("ResolvesHttpUrl", () => {
    const result = resolveSource("http://example.com/SKILL.md");
    expect(result.type).toBe("url");
    expect(result.resolved).toBe("http://example.com/SKILL.md");
  });

  it("ResolvesGitHubShorthand", () => {
    const result = resolveSource("github:user/repo/skills/my-skill");
    expect(result.type).toBe("url");
    expect(result.resolved).toContain("raw.githubusercontent.com");
    expect(result.resolved).toContain("user/repo/skills/my-skill");
  });

  it("ResolvesLocalPath", () => {
    const result = resolveSource("/path/to/SKILL.md");
    expect(result.type).toBe("local");
    expect(result.resolved).toBe("/path/to/SKILL.md");
  });

  it("ResolvesRelativePath", () => {
    const result = resolveSource("./skills/SKILL.md");
    expect(result.type).toBe("local");
    expect(result.resolved).toBe("./skills/SKILL.md");
  });
});

describe("installSkill", () => {
  let skillsDir: string;
  let configPath: string;
  let sourceDir: string;

  beforeEach(() => {
    skillsDir = join(tmpdir(), `install-skills-${randomUUID()}`);
    configPath = join(tmpdir(), `install-config-${randomUUID()}.jsonc`);
    sourceDir = join(tmpdir(), `install-source-${randomUUID()}`);
    mkdirSync(skillsDir, { recursive: true });
    mkdirSync(sourceDir, { recursive: true });
  });

  afterEach(() => {
    for (const dir of [skillsDir, sourceDir]) {
      if (existsSync(dir)) {
        rmSync(dir, { recursive: true, force: true });
      }
    }
    if (existsSync(configPath)) {
      rmSync(configPath, { force: true });
    }
  });

  it("InstallsFromLocalPath", async () => {
    const sourcePath = join(sourceDir, "SKILL.md");
    writeFileSync(
      sourcePath,
      `---
name: test-skill
description: A test skill
---
# Test Skill Content`
    );

    const result = await installSkill(sourcePath, { skillsDir, configPath });

    expect(result.name).toBe("test-skill");
    expect(result.description).toBe("A test skill");
    expect(existsSync(join(skillsDir, "test-skill", "SKILL.md"))).toBe(true);
  });

  it("ErrorsOnInvalidFrontmatter", async () => {
    const sourcePath = join(sourceDir, "SKILL.md");
    writeFileSync(sourcePath, "No frontmatter here");

    await expect(
      installSkill(sourcePath, { skillsDir, configPath })
    ).rejects.toThrow("missing or invalid YAML frontmatter");
  });

  it("ErrorsOnNonExistentLocalFile", async () => {
    await expect(
      installSkill("/non/existent/SKILL.md", { skillsDir, configPath })
    ).rejects.toThrow("File not found");
  });

  it("ErrorsWhenAlreadyInstalled", async () => {
    const sourcePath = join(sourceDir, "SKILL.md");
    writeFileSync(
      sourcePath,
      `---\nname: dup-skill\ndescription: Duplicate\n---\n# Content`
    );

    // First install
    await installSkill(sourcePath, { skillsDir, configPath });

    // Second install should fail
    await expect(
      installSkill(sourcePath, { skillsDir, configPath })
    ).rejects.toThrow("already installed");
  });

  it("OverwritesWithForce", async () => {
    const sourcePath = join(sourceDir, "SKILL.md");
    writeFileSync(
      sourcePath,
      `---\nname: force-skill\ndescription: Original\n---\n# V1`
    );

    await installSkill(sourcePath, { skillsDir, configPath });

    // Update content
    writeFileSync(
      sourcePath,
      `---\nname: force-skill\ndescription: Updated\n---\n# V2`
    );

    const result = await installSkill(sourcePath, {
      skillsDir,
      configPath,
      force: true,
    });

    expect(result.description).toBe("Updated");
    const content = readFileSync(
      join(skillsDir, "force-skill", "SKILL.md"),
      "utf-8"
    );
    expect(content).toContain("V2");
  });

  it("UpdatesAgentMappings", async () => {
    const sourcePath = join(sourceDir, "SKILL.md");
    writeFileSync(
      sourcePath,
      `---\nname: agent-skill\ndescription: Test\n---\n# Content`
    );

    await installSkill(sourcePath, {
      skillsDir,
      configPath,
      agents: ["tapestry", "shuttle"],
    });

    expect(existsSync(configPath)).toBe(true);
    const config = parseJsonc(readFileSync(configPath, "utf-8")) as {
      agents: Record<string, { skills: string[] }>;
    };
    expect(config.agents.tapestry.skills).toContain("agent-skill");
    expect(config.agents.shuttle.skills).toContain("agent-skill");
  });

  it("DoesNotDuplicateAgentMappings", async () => {
    // Pre-create config with existing mapping
    writeFileSync(
      configPath,
      JSON.stringify({
        agents: { tapestry: { skills: ["existing-skill"] } },
      })
    );

    const sourcePath = join(sourceDir, "SKILL.md");
    writeFileSync(
      sourcePath,
      `---\nname: new-skill\ndescription: Test\n---\n# Content`
    );

    await installSkill(sourcePath, {
      skillsDir,
      configPath,
      agents: ["tapestry"],
    });

    const config = parseJsonc(readFileSync(configPath, "utf-8")) as {
      agents: Record<string, { skills: string[] }>;
    };
    expect(config.agents.tapestry.skills).toContain("existing-skill");
    expect(config.agents.tapestry.skills).toContain("new-skill");
  });
});

describe("removeSkill", () => {
  let skillsDir: string;
  let configPath: string;

  beforeEach(() => {
    skillsDir = join(tmpdir(), `remove-skills-${randomUUID()}`);
    configPath = join(tmpdir(), `remove-config-${randomUUID()}.jsonc`);
    mkdirSync(skillsDir, { recursive: true });
  });

  afterEach(() => {
    if (existsSync(skillsDir)) {
      rmSync(skillsDir, { recursive: true, force: true });
    }
    if (existsSync(configPath)) {
      rmSync(configPath, { force: true });
    }
  });

  it("RemovesInstalledSkill", () => {
    const skillDir = join(skillsDir, "remove-me");
    mkdirSync(skillDir);
    writeFileSync(
      join(skillDir, "SKILL.md"),
      `---\nname: remove-me\ndescription: To remove\n---\n# Content`
    );

    removeSkill("remove-me", { skillsDir, configPath });

    expect(existsSync(skillDir)).toBe(false);
  });

  it("CleansUpAgentMappings", () => {
    const skillDir = join(skillsDir, "mapped-skill");
    mkdirSync(skillDir);
    writeFileSync(
      join(skillDir, "SKILL.md"),
      `---\nname: mapped-skill\ndescription: Mapped\n---\n# Content`
    );

    writeFileSync(
      configPath,
      JSON.stringify({
        agents: {
          tapestry: { skills: ["mapped-skill", "other-skill"] },
          shuttle: { skills: ["mapped-skill"] },
        },
      })
    );

    removeSkill("mapped-skill", { skillsDir, configPath });

    const config = parseJsonc(readFileSync(configPath, "utf-8")) as {
      agents: Record<string, { skills: string[] }>;
    };
    expect(config.agents.tapestry.skills).not.toContain("mapped-skill");
    expect(config.agents.tapestry.skills).toContain("other-skill");
    expect(config.agents.shuttle.skills).not.toContain("mapped-skill");
  });

  it("ErrorsForNonExistentSkill", () => {
    expect(() =>
      removeSkill("nonexistent", { skillsDir, configPath })
    ).toThrow("not installed");
  });
});
