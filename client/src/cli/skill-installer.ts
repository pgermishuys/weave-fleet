/**
 * Skill installer module.
 * Handles downloading, validating, and installing SKILL.md files from various sources.
 */

import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "fs";
import { join, dirname } from "path";
import { getSkillsDir, getUserWeaveConfigPath } from "./config-paths";
import {
  parseFrontmatter,
  readWeaveConfig,
  type WeaveConfig,
} from "./skill-catalog";

export interface InstallResult {
  name: string;
  description: string;
  path: string;
  source: string;
}

/**
 * Resolve a source string to a fetchable URL or local path.
 * Supports:
 *   - Raw URLs (https://...)
 *   - GitHub shorthand (github:user/repo/path/to/SKILL.md)
 *   - Local file paths (/path/to/SKILL.md)
 */
export function resolveSource(source: string): {
  type: "url" | "local";
  resolved: string;
} {
  if (source.startsWith("github:")) {
    const path = source.slice("github:".length);
    return {
      type: "url",
      resolved: `https://raw.githubusercontent.com/${path.replace(/^\//, "")}/HEAD/SKILL.md`,
    };
  }

  if (source.startsWith("http://") || source.startsWith("https://")) {
    return { type: "url", resolved: source };
  }

  return { type: "local", resolved: source };
}

/**
 * Fetch content from a URL.
 */
async function fetchContent(url: string): Promise<string> {
  const response = await fetch(url);
  if (!response.ok) {
    throw new Error(
      `Failed to fetch ${url}: ${response.status} ${response.statusText}`
    );
  }
  return response.text();
}

/**
 * Install a skill from a source (URL or local path).
 *
 * @param source - URL, github: shorthand, or local file path
 * @param options - Installation options
 * @returns The install result with name, description, and path
 */
export async function installSkill(
  source: string,
  options: {
    force?: boolean;
    agents?: string[];
    skillsDir?: string;
    configPath?: string;
  } = {}
): Promise<InstallResult> {
  const { force = false, agents, skillsDir, configPath } = options;
  const dir = skillsDir ?? getSkillsDir();
  const cfgPath = configPath ?? getUserWeaveConfigPath();

  const resolved = resolveSource(source);
  let content: string;

  if (resolved.type === "url") {
    content = await fetchContent(resolved.resolved);
  } else {
    if (!existsSync(resolved.resolved)) {
      throw new Error(`File not found: ${resolved.resolved}`);
    }
    content = readFileSync(resolved.resolved, "utf-8");
  }

  // Parse and validate frontmatter
  const frontmatter = parseFrontmatter(content);
  if (!frontmatter) {
    throw new Error(
      "Invalid SKILL.md: missing or invalid YAML frontmatter. Expected --- fences with at least a 'name' field."
    );
  }
  if (!frontmatter.name) {
    throw new Error("Invalid SKILL.md: 'name' field is required in frontmatter.");
  }

  const skillDir = join(dir, frontmatter.name);

  // Check if already installed
  if (existsSync(skillDir) && !force) {
    throw new Error(
      `Skill '${frontmatter.name}' is already installed at ${skillDir}. Use --force to overwrite.`
    );
  }

  // Create directory and write SKILL.md
  mkdirSync(skillDir, { recursive: true });
  writeFileSync(join(skillDir, "SKILL.md"), content, "utf-8");

  // Update agent mappings if requested
  if (agents && agents.length > 0) {
    addSkillToAgents(frontmatter.name, agents, cfgPath);
  }

  return {
    name: frontmatter.name,
    description: frontmatter.description,
    path: skillDir,
    source,
  };
}

/**
 * Remove an installed skill and clean up its agent assignments.
 */
export function removeSkill(
  name: string,
  options: {
    skillsDir?: string;
    configPath?: string;
  } = {}
): void {
  const { skillsDir, configPath } = options;
  const dir = skillsDir ?? getSkillsDir();
  const cfgPath = configPath ?? getUserWeaveConfigPath();

  const skillDir = join(dir, name);
  if (!existsSync(skillDir)) {
    throw new Error(`Skill '${name}' is not installed.`);
  }

  // Remove the skill directory
  rmSync(skillDir, { recursive: true, force: true });

  // Remove from agent mappings
  removeSkillFromAgents(name, cfgPath);
}

/**
 * Add a skill to the specified agents in weave-opencode.jsonc.
 */
function addSkillToAgents(
  skillName: string,
  agents: string[],
  configPath: string
): void {
  const config: WeaveConfig = readWeaveConfig(configPath) ?? {};

  if (!config.agents) {
    config.agents = {};
  }

  for (const agent of agents) {
    if (!config.agents[agent]) {
      config.agents[agent] = { skills: [] };
    }
    if (!config.agents[agent].skills) {
      config.agents[agent].skills = [];
    }
    if (!config.agents[agent].skills!.includes(skillName)) {
      config.agents[agent].skills!.push(skillName);
    }
  }

  writeConfigFile(configPath, config);
}

/**
 * Remove a skill from all agents in weave-opencode.jsonc.
 */
function removeSkillFromAgents(skillName: string, configPath: string): void {
  const config = readWeaveConfig(configPath);
  if (!config?.agents) return;

  let changed = false;
  for (const [, agentConfig] of Object.entries(config.agents)) {
    if (agentConfig.skills) {
      const idx = agentConfig.skills.indexOf(skillName);
      if (idx !== -1) {
        agentConfig.skills.splice(idx, 1);
        changed = true;
      }
    }
  }

  if (changed) {
    writeConfigFile(configPath, config);
  }
}

/**
 * Write a WeaveConfig to a JSONC file with a header comment.
 */
function writeConfigFile(configPath: string, config: WeaveConfig): void {
  const dir = dirname(configPath);
  mkdirSync(dir, { recursive: true });

  const content = JSON.stringify(config, null, 2);
  writeFileSync(configPath, content, "utf-8");
}
