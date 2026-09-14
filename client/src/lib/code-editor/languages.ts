import { LanguageDescription, type LanguageSupport } from "@codemirror/language";
import { languages } from "@codemirror/language-data";

/** Files that language-data doesn't know by extension, mapped to one it does. */
const EXTENSION_ALIASES: Record<string, string> = {
  csproj: "xml",
  fsproj: "xml",
  vbproj: "xml",
  props: "xml",
  targets: "xml",
  slnx: "xml",
  nuspec: "xml",
  xaml: "xml",
  axaml: "xml",
  resx: "xml",
  plist: "xml",
  jsonc: "json",
  json5: "json",
  zsh: "sh",
  env: "properties",
  editorconfig: "properties",
};

function fileName(path: string): string {
  return path.slice(path.lastIndexOf("/") + 1);
}

/** The language for a file, by name, or null for plain text. */
export function languageFor(path: string): LanguageDescription | null {
  const name = fileName(path);
  const direct = LanguageDescription.matchFilename(languages, name);
  if (direct) return direct;

  // ".env.local" and dotfiles such as ".editorconfig" count by their first extension too.
  const parts = name.toLowerCase().split(".").filter(Boolean);
  for (const part of parts.reverse()) {
    const alias = EXTENSION_ALIASES[part];
    if (alias) return LanguageDescription.matchFilename(languages, `file.${alias}`);
  }

  return null;
}

/** Load the language for a file. Each language is its own chunk, fetched the first time it's needed. */
export async function loadLanguage(path: string): Promise<LanguageSupport | null> {
  const description = languageFor(path);
  if (!description) return null;

  try {
    return description.support ?? (await description.load());
  } catch {
    // A language that fails to load leaves the file as plain text.
    return null;
  }
}
