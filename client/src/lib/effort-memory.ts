/**
 * The reasoning effort remembered for a model (a server preference per machine, so phone and desktop agree).
 * One key per harness and model; a model's slash becomes a colon so the key sits in one URL segment.
 * "Default" (nothing picked) is stored as an empty value, which reads as no memory.
 */
/** The catalog's selection key is a JSON pair `["provider","model"]`; the memory key reads `provider:model`. */
function modelName(modelKey: string): string {
  try {
    const parsed: unknown = JSON.parse(modelKey);
    if (Array.isArray(parsed) && parsed.every((part) => typeof part === "string")) return parsed.join(":");
  } catch {
    // Not a pair: use the key as it is.
  }
  return modelKey;
}

export function effortPreferenceKey(harnessType: string | undefined, modelKey: string): string {
  return `effort.${harnessType ?? "unknown"}.${modelName(modelKey).replaceAll("/", ":")}`;
}

/** The remembered level when the model still offers it, else "" (Default). A level the model dropped is ignored, not deleted. */
export function rememberedEffort(stored: string | undefined, variants: readonly string[]): string {
  return stored && variants.includes(stored) ? stored : "";
}

export function effortLabel(level: string): string {
  return level ? level.charAt(0).toUpperCase() + level.slice(1) : "Default";
}
