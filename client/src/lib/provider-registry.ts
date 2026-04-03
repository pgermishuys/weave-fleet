/**
 * Bundled provider registry — hardcoded list of known OpenCode providers
 * with their display names and common models.
 *
 * This is a pure data module — no I/O, no auth checking.
 * Provider IDs match the keys used in OpenCode's auth.json.
 */

export interface ProviderModelInfo {
  id: string;       // e.g. "claude-sonnet-4-5"
  name: string;     // e.g. "Claude Sonnet 4.5"
}

export interface BundledProvider {
  id: string;           // e.g. "anthropic" — matches auth.json key
  name: string;         // e.g. "Anthropic"
  models: ProviderModelInfo[];
}

export const BUNDLED_PROVIDERS: BundledProvider[] = [
  {
    id: "anthropic",
    name: "Anthropic",
    models: [
      { id: "claude-sonnet-4-5", name: "Claude Sonnet 4.5" },
      { id: "claude-sonnet-4-20250514", name: "Claude Sonnet 4" },
      { id: "claude-opus-4-20250514", name: "Claude Opus 4" },
      { id: "claude-haiku-3-5-20241022", name: "Claude 3.5 Haiku" },
    ],
  },
  {
    id: "openai",
    name: "OpenAI",
    models: [
      { id: "gpt-4.1", name: "GPT-4.1" },
      { id: "gpt-4.1-mini", name: "GPT-4.1 Mini" },
      { id: "gpt-4.1-nano", name: "GPT-4.1 Nano" },
      { id: "o3", name: "o3" },
      { id: "o4-mini", name: "o4 Mini" },
    ],
  },
  {
    id: "google",
    name: "Google",
    models: [
      { id: "gemini-2.5-pro", name: "Gemini 2.5 Pro" },
      { id: "gemini-2.5-flash", name: "Gemini 2.5 Flash" },
      { id: "gemini-2.0-flash", name: "Gemini 2.0 Flash" },
    ],
  },
  {
    id: "amazon-bedrock",
    name: "Amazon Bedrock",
    models: [
      { id: "us.anthropic.claude-sonnet-4-5-20250514-v1:0", name: "Claude Sonnet 4.5 (Bedrock)" },
      { id: "us.anthropic.claude-sonnet-4-20250514-v1:0", name: "Claude Sonnet 4 (Bedrock)" },
    ],
  },
  {
    id: "azure",
    name: "Azure OpenAI",
    models: [
      { id: "gpt-4.1", name: "GPT-4.1 (Azure)" },
      { id: "gpt-4.1-mini", name: "GPT-4.1 Mini (Azure)" },
    ],
  },
  {
    id: "xai",
    name: "xAI",
    models: [
      { id: "grok-3", name: "Grok 3" },
      { id: "grok-3-mini", name: "Grok 3 Mini" },
    ],
  },
  {
    id: "mistral",
    name: "Mistral",
    models: [
      { id: "codestral-latest", name: "Codestral" },
      { id: "mistral-large-latest", name: "Mistral Large" },
    ],
  },
  {
    id: "groq",
    name: "Groq",
    models: [
      { id: "llama-3.3-70b-versatile", name: "Llama 3.3 70B" },
    ],
  },
  {
    id: "github-copilot",
    name: "GitHub Copilot",
    models: [
      { id: "claude-sonnet-4", name: "Claude Sonnet 4 (Copilot)" },
      { id: "gpt-4.1", name: "GPT-4.1 (Copilot)" },
      { id: "o4-mini", name: "o4 Mini (Copilot)" },
    ],
  },
];

/** Lookup helper: get a BundledProvider by its id */
export function getProviderById(id: string): BundledProvider | undefined {
  return BUNDLED_PROVIDERS.find((p) => p.id === id);
}
