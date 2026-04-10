import type { SessionSourceDescriptor } from "@/lib/api-types";
import type { RegisteredSessionSource, SessionSourceUiContribution } from "./types";

function makeSourceId(providerId: string, sourceType: string): string {
  return `${providerId}:${sourceType}`;
}

export function mergeSessionSources(
  backendSources: readonly SessionSourceDescriptor[],
  contributions: readonly SessionSourceUiContribution[]
): RegisteredSessionSource[] {
  const contributionMap = new Map<string, SessionSourceUiContribution>();
  for (const contribution of contributions) {
    contributionMap.set(makeSourceId(contribution.sourceKey.providerId, contribution.sourceKey.sourceType), contribution);
  }

  return [...backendSources]
    .map((descriptor) => {
      const key = makeSourceId(descriptor.key.providerId, descriptor.key.sourceType);
      const contribution = contributionMap.get(key);
      return {
        descriptor,
        pluginId: contribution?.pluginId,
        contributionId: contribution?.id,
        label: contribution?.label ?? descriptor.displayName,
        description: contribution?.description ?? null,
        icon: contribution?.icon,
        order: contribution?.order ?? 0,
        formComponent: contribution?.formComponent,
      } satisfies RegisteredSessionSource;
    })
    .sort((left, right) => {
      if (left.order !== right.order) {
        return left.order - right.order;
      }

      return left.label.localeCompare(right.label);
    });
}
