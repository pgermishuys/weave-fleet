/**
 * `weave-fleet skill` subcommands: list, install, remove.
 */

import { listInstalledSkills } from "./skill-catalog";
import { installSkill, removeSkill } from "./skill-installer";

/**
 * Run `skill list` — prints all installed skills with descriptions and agent assignments.
 */
export function runSkillList(): void {
  const skills = listInstalledSkills();

  if (skills.length === 0) {
    console.log("No skills installed.");
    console.log(
      "Install skills with: weave-fleet skill install <url-or-path>"
    );
    return;
  }

  console.log(`\nInstalled Skills (${skills.length}):\n`);

  // Calculate column widths for alignment
  const maxNameLen = Math.max(...skills.map((s) => s.name.length));

  for (const skill of skills) {
    const agents =
      skill.assignedAgents.length > 0
        ? ` -> ${skill.assignedAgents.join(", ")}`
        : "";
    const name = skill.name.padEnd(maxNameLen + 2);
    console.log(`  ${name}${skill.description}${agents}`);
  }
  console.log();
}

/**
 * Run `skill install <source>` — downloads and installs a SKILL.md.
 */
export async function runSkillInstall(
  source: string,
  options: { force?: boolean; agents?: string[] } = {}
): Promise<void> {
  try {
    const result = await installSkill(source, options);
    console.log(`\nInstalled skill: ${result.name}`);
    console.log(`  Description: ${result.description}`);
    console.log(`  Path: ${result.path}`);

    if (options.agents && options.agents.length > 0) {
      console.log(`  Assigned to agents: ${options.agents.join(", ")}`);
    }

    console.log();
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    console.error(`Error: ${message}`);
    process.exit(1);
  }
}

/**
 * Run `skill remove <name>` — removes an installed skill.
 */
export function runSkillRemove(name: string): void {
  try {
    removeSkill(name);
    console.log(`Removed skill: ${name}`);
  } catch (error) {
    const message = error instanceof Error ? error.message : String(error);
    console.error(`Error: ${message}`);
    process.exit(1);
  }
}
