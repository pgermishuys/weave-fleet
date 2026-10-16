import type { ModelOption } from "@/composables/use-models";
import { modelPickerKey } from "@/stores/model-picker";

export interface ModelSection {
  title?: string;
  level: "group" | "provider";
  items: ModelOption[];
}

/** More models than this from several providers get a header per provider. */
const PROVIDER_HEADERS_ABOVE = 8;
const RECENT_SHOWN = 5;

/** The picker's unfiltered list: Favourites, Recent, then everything (a favourite stays in it too). */
export function groupModels(
  models: readonly ModelOption[],
  harnessType: string,
  favourites: readonly string[],
  recent: readonly string[],
  showRecent: boolean,
): ModelSection[] {
  const byKey = new Map(models.map((model) => [modelPickerKey(harnessType, model.providerId, model.id), model]));
  const listed = (keys: readonly string[]): ModelOption[] =>
    keys.flatMap((key) => byKey.get(key) ?? []);

  const favouriteItems = listed(favourites);
  const harnessPrefix = `${harnessType}:`;
  const recentItems = showRecent
    ? listed(recent.filter((key) => key.startsWith(harnessPrefix) && !favourites.includes(key))).slice(0, RECENT_SHOWN)
    : [];

  const sections: ModelSection[] = [];
  if (favouriteItems.length > 0) sections.push({ title: "Favourites", level: "group", items: favouriteItems });
  if (recentItems.length > 0) sections.push({ title: "Recent", level: "group", items: recentItems });

  const titled = sections.length > 0;
  const providers = [...new Set(models.map((model) => model.provider))];
  if (models.length > PROVIDER_HEADERS_ABOVE && providers.length > 1) {
    // The "All models" header stands alone above its provider headers.
    if (titled) sections.push({ title: "All models", level: "group", items: [] });
    for (const provider of providers) {
      sections.push({ title: provider, level: "provider", items: models.filter((model) => model.provider === provider) });
    }
    return sections;
  }

  sections.push({ title: titled ? "All models" : undefined, level: "group", items: [...models] });
  return sections;
}
