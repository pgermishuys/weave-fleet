/**
 * Utility functions for markdown rendering.
 * Pure functions with no React dependencies — safe to import anywhere and test in isolation.
 */

/**
 * Extracts the programming language identifier from a highlight.js class string.
 *
 * `rehype-highlight` applies classes in the format `"hljs language-{lang}"`.
 * This function handles both `"language-{lang}"` and `"hljs language-{lang}"` formats.
 *
 * @param className - The `className` prop from the rendered `<code>` element.
 * @returns The language identifier (e.g. `"typescript"`, `"python"`) or `""` if absent.
 */
export function extractLanguage(className: string | undefined): string {
  if (!className) return "";
  const match = className.match(/language-(\S+)/);
  return match ? match[1] : "";
}

/**
 * Recursively extracts plain text content from a React node tree.
 * Used to obtain the raw code string for clipboard operations.
 *
 * @param node - Any React node (string, number, array, element, or null/undefined).
 * @returns The concatenated plain-text content.
 */
export function extractText(node: unknown): string {
  if (typeof node === "string") return node;
  if (typeof node === "number") return String(node);
  if (!node) return "";
  if (Array.isArray(node)) return (node as unknown[]).map(extractText).join("");
  if (typeof node === "object" && node !== null && "props" in node) {
    const el = node as { props?: { children?: unknown } };
    return extractText(el.props?.children);
  }
  return "";
}
