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
  listInstalledSkills,
  parseFrontmatter,
  parseJsonc,
  readWeaveConfig,
} from "../skill-catalog";

describe("parseFrontmatter", () => {
  it("ParsesValidFrontmatter", () => {
    const content = `---
name: my-skill
description: A test skill for testing purposes
---

# My Skill
Content here.`;

    const result = parseFrontmatter(content);

    expect(result).not.toBeNull();
    expect(result!.name).toBe("my-skill");
    expect(result!.description).toBe("A test skill for testing purposes");
  });

  it("ReturnsNullForMissingFences", () => {
    const content = "name: my-skill\ndescription: No fences";
    expect(parseFrontmatter(content)).toBeNull();
  });

  it("ReturnsNullForMissingEndFence", () => {
    const content = "---\nname: my-skill\n";
    expect(parseFrontmatter(content)).toBeNull();
  });

  it("ReturnsNullForMissingName", () => {
    const content = "---\ndescription: No name\n---\n";
    expect(parseFrontmatter(content)).toBeNull();
  });

  it("HandlesExtraWhitespace", () => {
    const content = `---
name:   padded-name  
description:   padded description  
---`;

    const result = parseFrontmatter(content);

    expect(result).not.toBeNull();
    expect(result!.name).toBe("padded-name");
    expect(result!.description).toBe("padded description");
  });
});

describe("parseJsonc", () => {
  it("ParsesPlainJson", () => {
    const result = parseJsonc('{"key": "value"}');
    expect(result).toEqual({ key: "value" });
  });

  it("StripsSingleLineComments", () => {
    const content = `{
  // This is a comment
  "key": "value"
}`;
    const result = parseJsonc(content) as Record<string, string>;
    expect(result.key).toBe("value");
  });

  it("StripsMultiLineComments", () => {
    const content = `{
  /* Multi-line
     comment */
  "key": "value"
}`;
    const result = parseJsonc(content) as Record<string, string>;
    expect(result.key).toBe("value");
  });

  it("PreservesSlashesInStrings", () => {
    const content = '{"url": "https://example.com"}';
    const result = parseJsonc(content) as Record<string, string>;
    expect(result.url).toBe("https://example.com");
  });

  it("HandlesTrailingCommas", () => {
    const content = `{
  "a": 1,
  "b": 2,
}`;
    const result = parseJsonc(content) as Record<string, number>;
    expect(result.a).toBe(1);
    expect(result.b).toBe(2);
  });

  it("HandlesTrailingCommasInArrays", () => {
    const content = '{"arr": [1, 2, 3,]}';
    const result = parseJsonc(content) as Record<string, number[]>;
    expect(result.arr).toEqual([1, 2, 3]);
  });
});

describe("readWeaveConfig", () => {
  let testDir: string;

  beforeEach(() => {
    testDir = join(tmpdir(), `config-test-${randomUUID()}`);
    mkdirSync(testDir, { recursive: true });
  });

  afterEach(() => {
    if (existsSync(testDir)) {
      rmSync(testDir, { recursive: true, force: true });
    }
  });

  it("ReturnsNullForNonExistentFile", () => {
    const result = readWeaveConfig(join(testDir, "nonexistent.jsonc"));
    expect(result).toBeNull();
  });

  it("ReadsValidConfig", () => {
    const configPath = join(testDir, "config.jsonc");
    writeFileSync(
      configPath,
      `// comment
{
  "agents": {
    "tapestry": {
      "skills": ["skill-a", "skill-b"]
    }
  }
}`,
      "utf-8"
    );

    const result = readWeaveConfig(configPath);

    expect(result).not.toBeNull();
    expect(result!.agents!.tapestry.skills).toEqual(["skill-a", "skill-b"]);
  });
});

describe("listInstalledSkills", () => {
  let skillsDir: string;
  let configPath: string;

  beforeEach(() => {
    skillsDir = join(tmpdir(), `skills-test-${randomUUID()}`);
    configPath = join(tmpdir(), `config-test-${randomUUID()}.jsonc`);
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

  it("ReturnsEmptyForEmptyDirectory", () => {
    const result = listInstalledSkills(skillsDir, configPath);
    expect(result).toEqual([]);
  });

  it("ReturnsEmptyForNonExistentDirectory", () => {
    const result = listInstalledSkills("/non/existent/dir", configPath);
    expect(result).toEqual([]);
  });

  it("ListsSkillsWithFrontmatter", () => {
    // Create two skills
    const skillA = join(skillsDir, "skill-a");
    mkdirSync(skillA);
    writeFileSync(
      join(skillA, "SKILL.md"),
      `---
name: skill-a
description: First skill
---
# Skill A`
    );

    const skillB = join(skillsDir, "skill-b");
    mkdirSync(skillB);
    writeFileSync(
      join(skillB, "SKILL.md"),
      `---
name: skill-b
description: Second skill
---
# Skill B`
    );

    const result = listInstalledSkills(skillsDir, configPath);

    expect(result).toHaveLength(2);
    expect(result[0].name).toBe("skill-a");
    expect(result[0].description).toBe("First skill");
    expect(result[1].name).toBe("skill-b");
    expect(result[1].description).toBe("Second skill");
  });

  it("SkipsDirectoriesWithoutSkillMd", () => {
    const noSkill = join(skillsDir, "no-skill");
    mkdirSync(noSkill);
    writeFileSync(join(noSkill, "README.md"), "# Not a skill");

    const result = listInstalledSkills(skillsDir, configPath);
    expect(result).toEqual([]);
  });

  it("SkipsInvalidFrontmatter", () => {
    const badSkill = join(skillsDir, "bad-skill");
    mkdirSync(badSkill);
    writeFileSync(join(badSkill, "SKILL.md"), "No frontmatter here");

    const result = listInstalledSkills(skillsDir, configPath);
    expect(result).toEqual([]);
  });

  it("MergesAgentAssignments", () => {
    const skillDir = join(skillsDir, "my-skill");
    mkdirSync(skillDir);
    writeFileSync(
      join(skillDir, "SKILL.md"),
      `---
name: my-skill
description: Test skill
---
# Content`
    );

    writeFileSync(
      configPath,
      JSON.stringify({
        agents: {
          tapestry: { skills: ["my-skill", "other-skill"] },
          shuttle: { skills: ["my-skill"] },
          weft: { skills: ["other-skill"] },
        },
      })
    );

    const result = listInstalledSkills(skillsDir, configPath);

    expect(result).toHaveLength(1);
    expect(result[0].assignedAgents).toContain("tapestry");
    expect(result[0].assignedAgents).toContain("shuttle");
    expect(result[0].assignedAgents).not.toContain("weft");
  });

  it("SortsSkillsByName", () => {
    for (const name of ["zebra-skill", "alpha-skill", "middle-skill"]) {
      const dir = join(skillsDir, name);
      mkdirSync(dir);
      writeFileSync(
        join(dir, "SKILL.md"),
        `---\nname: ${name}\ndescription: A ${name}\n---\n# Content`
      );
    }

    const result = listInstalledSkills(skillsDir, configPath);

    expect(result[0].name).toBe("alpha-skill");
    expect(result[1].name).toBe("middle-skill");
    expect(result[2].name).toBe("zebra-skill");
  });
});
