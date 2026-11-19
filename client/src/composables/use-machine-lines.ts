import { storeToRefs } from "pinia";
import { useMachinesStore, type MachineEntry, type MachineHarness } from "@/stores/machines";
import { isTailscale, kindOf, type MachineAddress } from "@/lib/machine-routes";
import { reachLine, type ReachLine } from "@/lib/machine-reach-line";
import { useMachineReach } from "@/composables/use-machine-reach";
import { tunnelDownDiagnosis, type MachineDiagnosis } from "@/lib/machine-diagnosis";

/** The lines Settings → Machines draws about a machine, in its list and on its own page. */
export function useMachineLines() {
  const machines = useMachinesStore();
  const { others, liveReachable } = storeToRefs(machines);
  const reach = useMachineReach();

  /** Why a machine isn't answering, when known: its tunnel being down, or what home or this page found. */
  function diagnosisFor(entry: MachineEntry): MachineDiagnosis | null {
    return entry.tunnelUp === false ? tunnelDownDiagnosis(entry.name, entry.sshDestination ?? null) : machines.diagnoses[entry.key] ?? null;
  }

  function stateLine(entry: MachineEntry): { text: string; tone: "ok" | "bad" | "muted" } {
    if (entry.tunnelUp === false) return { text: diagnosisFor(entry)!.headline, tone: "bad" };
    if (entry.isLive) return { text: "live", tone: "ok" };
    const state = others.value[entry.key];
    if (state?.error) return { text: diagnosisFor(entry)?.headline ?? state.error, tone: "bad" };
    if (state?.loadedAt) return { text: `reachable · ${state.sessions.length} ${state.sessions.length === 1 ? "session" : "sessions"}`, tone: "ok" };
    return { text: "checking…", tone: "muted" };
  }

  /** Whether the machine answers this client now. */
  function answering(entry: MachineEntry): boolean {
    if (entry.tunnelUp === false) return false;
    return entry.isLive ? entry.isHome || liveReachable.value : !others.value[entry.key]?.error;
  }

  function readyHarnesses(entry: MachineEntry): MachineHarness[] {
    return entry.capabilities?.harnesses?.filter((harness) => harness.available && harness.enabled) ?? [];
  }

  /**
   * What the machine last said it can run (its ready harnesses) and how busy it is. Nothing for a Fleet too old to say,
   * or one that isn't answering now: its last numbers would be out of date.
   */
  function capabilityLine(entry: MachineEntry): string | null {
    const capabilities = entry.capabilities;
    if (!capabilities || !(entry.isLive ? entry.isHome || liveReachable.value : !others.value[entry.key]?.error)) return null;

    const parts: string[] = [];
    if (capabilities.harnesses) {
      const ready = readyHarnesses(entry).map((harness) => harness.name);
      parts.push(ready.length ? `Ready: ${ready.join(", ")}` : "No harness ready");
    }
    const { working, needsYou } = capabilities.sessions;
    parts.push(`${working} working`);
    if (needsYou) parts.push(`${needsYou} ${needsYou === 1 ? "needs" : "need"} you`);
    return parts.join(" · ");
  }

  function harnessVersions(entry: MachineEntry): string | undefined {
    const versions = readyHarnesses(entry).map((harness) => harness.version ? `${harness.name} ${harness.version}` : harness.name);
    return versions.length ? versions.join(", ") : undefined;
  }

  /** The addresses of a saved machine reached by URL; none for home or one reached over SSH. */
  function addressesOf(entry: MachineEntry): MachineAddress[] {
    if (entry.isHome || entry.sshDestination || entry.connection?.sshDestination) return [];
    return entry.connection?.addresses ?? [];
  }

  /** How this tab is connected, while the machine answers: from the kind of the address in use. */
  function routeLine(entry: MachineEntry): { text: string; note: string | null } | null {
    const addresses = addressesOf(entry);
    if (!addresses.length || stateLine(entry).tone !== "ok") return null;
    const kind = kindOf(entry.baseUrl);
    const text = isTailscale(kind) ? "Connected over Tailscale"
      : kind === "lan" ? "Connected on your network"
        : kind === "hostname" ? "Connected by host name"
          : `Connected at ${entry.baseUrl}`;
    const first = addresses[0];
    // Only when Fleet fell back off Tailscale: on its IP after its name failed, Tailscale itself works.
    const note = first.url !== entry.baseUrl && isTailscale(first.kind) && !isTailscale(kind) ? "Tailscale isn't answering" : null;
    return { text, note };
  }

  function location(entry: MachineEntry): string {
    return entry.isHome ? "this machine" : entry.baseUrl;
  }

  /**
   * How a phone reaches the machine. `homeAddresses` are the ones the machine this client is signed in to reports; the
   * other machines are told apart by the addresses home keeps for them.
   */
  function reachOf(entry: MachineEntry, homeAddresses: readonly Pick<MachineAddress, "kind">[]): ReachLine {
    const state = reach.state.value;
    const loadedAt = others.value[entry.key]?.loadedAt;
    return reachLine({
      name: entry.name,
      answering: answering(entry),
      offlineSince: loadedAt ?? null,
      addresses: entry.isHome ? homeAddresses : addressesOf(entry),
      reachMode: entry.isHome && state?.available ? state.mode : null,
      ownAddress: state?.ownAddress ?? null,
    });
  }

  return { reachOf, diagnosisFor, stateLine, answering, capabilityLine, harnessVersions, addressesOf, routeLine, location };
}
