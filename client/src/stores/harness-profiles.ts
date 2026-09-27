import { defineStore } from "pinia";
import { shallowRef } from "vue";
import { api, type HarnessProfile, type HarnessProfileCheck } from "@/api/client";
import type { MachineTarget } from "@/lib/machine-target";

/**
 * The user's harness profiles, by harness. Settings edits them and the new-session box picks from them, so both
 * read this one store: a profile made in Settings shows up in the picker straight away.
 */
export const useHarnessProfilesStore = defineStore("harness-profiles", () => {
  const byHarness = shallowRef<Readonly<Record<string, readonly HarnessProfile[]>>>({});
  /** Other machines' profiles, by machine key and harness: the new-session box can start a session on another machine. */
  const onOtherMachines = shallowRef<Readonly<Record<string, readonly HarnessProfile[]>>>({});
  const loading = new Map<string, Promise<void>>();

  const otherKey = (target: MachineTarget, harnessType: string) => `${target.key}\n${harnessType}`;
  const isOther = (target?: MachineTarget): target is MachineTarget => target !== undefined && !target.isLive;

  /** The live machine's profiles for a harness, or `target`'s. */
  function profilesFor(harnessType: string, target?: MachineTarget): readonly HarnessProfile[] {
    return isOther(target) ? onOtherMachines.value[otherKey(target, harnessType)] ?? [] : byHarness.value[harnessType] ?? [];
  }

  function defaultFor(harnessType: string, target?: MachineTarget): HarnessProfile | null {
    return profilesFor(harnessType, target).find((profile) => profile.isDefault) ?? null;
  }

  function put(harnessType: string, profiles: readonly HarnessProfile[]): void {
    byHarness.value = { ...byHarness.value, [harnessType]: profiles };
  }

  async function load(harnessType: string, target?: MachineTarget): Promise<void> {
    const loadingKey = isOther(target) ? otherKey(target, harnessType) : harnessType;
    const pending = loading.get(loadingKey);
    if (pending) return pending;

    const request = (async () => {
      const { data, response } = await (isOther(target) ? target.api : api).GET("/api/harnesses/{harnessType}/profiles", {
        params: { path: { harnessType } },
      });
      if (response.ok && Array.isArray(data)) {
        const profiles = data as unknown as HarnessProfile[];
        if (isOther(target)) {
          onOtherMachines.value = { ...onOtherMachines.value, [otherKey(target, harnessType)]: profiles };
        } else {
          put(harnessType, profiles);
        }
      }
    })().finally(() => loading.delete(loadingKey));
    loading.set(loadingKey, request);
    return request;
  }

  /** Saves a new profile. The server tries it with the harness first and refuses it with the harness's reason. */
  async function create(harnessType: string, name: string, content: string): Promise<HarnessProfile> {
    const { data, error, response } = await api.POST("/api/harnesses/{harnessType}/profiles", {
      params: { path: { harnessType } },
      body: { name, content },
    });
    if (!response.ok) throw new Error(errorMessage(error, response));
    const created = data as unknown as HarnessProfile;
    put(harnessType, [...profilesFor(harnessType), created]);
    return created;
  }

  async function update(harnessType: string, id: string, name: string, content: string): Promise<HarnessProfile> {
    const { data, error, response } = await api.PUT("/api/harnesses/{harnessType}/profiles/{id}", {
      params: { path: { harnessType, id } },
      body: { name, content },
    });
    if (!response.ok) throw new Error(errorMessage(error, response));
    const updated = data as unknown as HarnessProfile;
    put(harnessType, profilesFor(harnessType).map((profile) => (profile.id === id ? updated : profile)));
    return updated;
  }

  async function remove(harnessType: string, id: string): Promise<void> {
    const { error, response } = await api.DELETE("/api/harnesses/{harnessType}/profiles/{id}", {
      params: { path: { harnessType, id } },
    });
    if (!response.ok) throw new Error(errorMessage(error, response));
    put(harnessType, profilesFor(harnessType).filter((profile) => profile.id !== id));
  }

  /** Makes a profile the default for new sessions, or clears the default with null. */
  async function setDefault(harnessType: string, id: string | null): Promise<void> {
    const { error, response } = await api.PUT("/api/harnesses/{harnessType}/profiles/default", {
      params: { path: { harnessType } },
      body: { profileId: id },
    });
    if (!response.ok) throw new Error(errorMessage(error, response));
    put(harnessType, profilesFor(harnessType).map((profile) => ({ ...profile, isDefault: profile.id === id })));
  }

  /** Asks the harness to try content without saving it. */
  async function check(harnessType: string, content: string): Promise<HarnessProfileCheck> {
    const { data, error, response } = await api.POST("/api/harnesses/{harnessType}/profiles/check", {
      params: { path: { harnessType } },
      body: { content },
    });
    if (!response.ok) return { ok: false, error: errorMessage(error, response) };
    return data as unknown as HarnessProfileCheck;
  }

  return { byHarness, profilesFor, defaultFor, load, create, update, remove, setDefault, check };
});

function errorMessage(error: unknown, response: Response): string {
  if (error && typeof error === "object") {
    const record = error as Record<string, unknown>;
    for (const key of ["error", "message", "detail"]) {
      if (typeof record[key] === "string" && record[key]) return record[key] as string;
    }
  }
  return `HTTP ${response.status}`;
}
