/**
 * Project detection engine.
 * Scans a directory for language/framework indicators and returns a structured profile
 * with suggested skills based on detected technologies.
 */

import { readdirSync, existsSync, statSync } from "fs";
import { join } from "path";

export interface ProjectProfile {
  languages: string[];
  frameworks: string[];
  suggestedSkills: string[];
  isGitRepo: boolean;
}

interface DetectionRule {
  /** Glob-like patterns to check for (simple file existence or extension match) */
  indicators: Array<{
    type: "file" | "extension" | "directory";
    pattern: string;
  }>;
  language: string;
  framework?: string;
  skills: string[];
}

const DETECTION_RULES: DetectionRule[] = [
  {
    indicators: [
      { type: "extension", pattern: ".csproj" },
      { type: "extension", pattern: ".sln" },
      { type: "file", pattern: "Directory.Build.props" },
    ],
    language: "csharp",
    framework: "dotnet",
    skills: [
      "enforcing-csharp-standards",
      "enforcing-dotnet-testing",
      "reviewing-csharp-code",
      "verifying-release-builds",
    ],
  },
  {
    indicators: [
      { type: "file", pattern: "next.config.js" },
      { type: "file", pattern: "next.config.ts" },
      { type: "file", pattern: "next.config.mjs" },
    ],
    language: "typescript",
    framework: "nextjs",
    skills: [],
  },
  {
    indicators: [{ type: "file", pattern: "tsconfig.json" }],
    language: "typescript",
    framework: "nodejs",
    skills: [],
  },
  {
    indicators: [{ type: "file", pattern: "package.json" }],
    language: "javascript",
    framework: "nodejs",
    skills: [],
  },
  {
    indicators: [{ type: "file", pattern: "go.mod" }],
    language: "go",
    skills: [],
  },
  {
    indicators: [{ type: "file", pattern: "Cargo.toml" }],
    language: "rust",
    skills: [],
  },
  {
    indicators: [
      { type: "file", pattern: "pyproject.toml" },
      { type: "file", pattern: "setup.py" },
      { type: "file", pattern: "requirements.txt" },
    ],
    language: "python",
    skills: [],
  },
];

/**
 * Check if a directory contains a file matching the given indicator.
 */
function matchesIndicator(
  dirEntries: string[],
  indicator: { type: "file" | "extension" | "directory"; pattern: string },
  directory: string
): boolean {
  switch (indicator.type) {
    case "file":
      return dirEntries.includes(indicator.pattern);
    case "extension":
      return dirEntries.some((entry) => entry.endsWith(indicator.pattern));
    case "directory": {
      const dirPath = join(directory, indicator.pattern);
      return existsSync(dirPath) && statSync(dirPath).isDirectory();
    }
  }
}

/**
 * Detect project technologies and suggest appropriate skills.
 *
 * @param directory - The project root directory to scan
 * @returns A structured profile with detected languages, frameworks, and suggested skills
 */
export function detectProject(directory: string): ProjectProfile {
  if (!existsSync(directory)) {
    throw new Error(`Directory does not exist: ${directory}`);
  }

  if (!statSync(directory).isDirectory()) {
    throw new Error(`Not a directory: ${directory}`);
  }

  const entries = readdirSync(directory);
  const languages = new Set<string>();
  const frameworks = new Set<string>();
  const suggestedSkills = new Set<string>();

  // Check git repo status
  const isGitRepo =
    existsSync(join(directory, ".git")) &&
    statSync(join(directory, ".git")).isDirectory();

  // Always suggest managing-pull-requests for git repos
  if (isGitRepo) {
    suggestedSkills.add("managing-pull-requests");
  }

  // Run detection rules (ordered by specificity — more specific rules first)
  for (const rule of DETECTION_RULES) {
    const matched = rule.indicators.some((indicator) =>
      matchesIndicator(entries, indicator, directory)
    );

    if (matched) {
      languages.add(rule.language);
      if (rule.framework) {
        frameworks.add(rule.framework);
      }
      for (const skill of rule.skills) {
        suggestedSkills.add(skill);
      }
    }
  }

  return {
    languages: Array.from(languages),
    frameworks: Array.from(frameworks),
    suggestedSkills: Array.from(suggestedSkills),
    isGitRepo,
  };
}
