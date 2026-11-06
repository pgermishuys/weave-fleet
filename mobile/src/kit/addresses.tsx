// A machine's addresses in the order Fleet tries them, as the computer's Settings → Machines shows them: which one is
// in use, what came of the last try of each, and arrows to move one up or down.
import { AccessibilityInfo, Pressable, View } from "react-native";
import { canUse, KIND_LABELS, routeLine, type MachineAddress, type MachineAddressResult } from "~/fleet/routes-core";
import { useTheme } from "~/theme";
import { Pill } from "./controls";
import { Icon } from "./icons";
import { Text } from "./text";

const RESULTS: Record<MachineAddressResult, { label: string; tone: "muted" | "waiting" }> = {
  "ok": { label: "Answered", tone: "muted" },
  "no-answer": { label: "No answer", tone: "muted" },
  "other-machine": { label: "Another device", tone: "waiting" },
};

function statusOf(address: MachineAddress, inUse: string): { label: string; tone: "running" | "muted" | "waiting" } {
  if (address.url === inUse) return { label: "In use", tone: "running" };
  if (!canUse(address.url)) return { label: "Needs https", tone: "muted" };
  return address.lastResult ? RESULTS[address.lastResult] : { label: "Not tried", tone: "muted" };
}

/** How the phone is connected, under the machine's name: "Connected on your network · Tailscale isn't answering". */
export function RouteLine({ addresses, inUse }: { addresses: readonly MachineAddress[]; inUse: string }) {
  const { text, note } = routeLine(addresses, inUse);
  return (
    <Text tone="running" size={14} testID="machine-route">
      {text}
      {note ? <Text tone="muted" size={14}>{` · ${note}`}</Text> : null}
    </Text>
  );
}

function Arrow({ label, icon, disabled, onPress }: { label: string; icon: "chevron-up" | "chevron-down"; disabled: boolean; onPress: () => void }) {
  const { color } = useTheme();
  return (
    <Pressable
      accessibilityRole="button" accessibilityLabel={label} accessibilityState={{ disabled }} disabled={disabled} hitSlop={4} onPress={onPress}
      style={({ pressed }) => ({ width: 36, height: 36, alignItems: "center", justifyContent: "center", opacity: disabled ? 0.3 : pressed ? 0.6 : 1 })}
    >
      <Icon name={icon} size={18} color={color.muted} />
    </Pressable>
  );
}

/**
 * The list, numbered in the order Fleet tries it. `onMove` moves the address at `from` to `to` (no arrows without it);
 * with one address there's nothing to order, so it's shown plainly.
 */
export function AddressList({ addresses, inUse, onMove, busy }: {
  addresses: readonly MachineAddress[]; inUse: string; onMove?: (from: number, to: number) => void; busy?: boolean;
}) {
  const { color, radius } = useTheme();
  if (addresses.length < 2) {
    return <Text tone="muted" mono size={12} numberOfLines={1} ellipsizeMode="middle">{addresses[0]?.url ?? inUse}</Text>;
  }
  const move = (from: number, to: number) => {
    onMove?.(from, to);
    AccessibilityInfo.announceForAccessibility(`${KIND_LABELS[addresses[from].kind]} moved to position ${to + 1} of ${addresses.length}`);
  };
  return (
    <View style={{ gap: 8 }} testID="machine-addresses">
      <Text tone="muted" size={13}>Fleet tries these in order and goes back to a higher one as soon as it answers again.</Text>
      <View style={{ borderWidth: 1, borderColor: color.border, borderRadius: radius.card, overflow: "hidden" }} accessibilityLabel="Addresses, in the order Fleet tries them">
        {addresses.map((address, index) => {
          const status = statusOf(address, inUse);
          const kind = KIND_LABELS[address.kind];
          return (
            <View
              key={address.url}
              style={{
                flexDirection: "row", alignItems: "center", gap: 10, paddingLeft: 12, paddingRight: onMove ? 2 : 12, paddingVertical: 8,
                borderTopWidth: index === 0 ? 0 : 1, borderTopColor: color.border,
                backgroundColor: address.url === inUse ? color.tint5 : "transparent",
              }}
            >
              <Text tone="muted" mono size={12} style={{ width: 14 }}>{index + 1}</Text>
              <View style={{ flex: 1, gap: 2 }}>
                <View style={{ flexDirection: "row", alignItems: "center", gap: 8 }}>
                  <Text weight="medium" size={14} numberOfLines={1} style={{ flexShrink: 1 }}>{kind}</Text>
                  <Pill label={status.label} tone={status.tone} />
                </View>
                <Text tone="muted" mono size={12} numberOfLines={1} ellipsizeMode="middle">{address.url}</Text>
              </View>
              {onMove ? (
                <View style={{ flexDirection: "row" }}>
                  <Arrow label={`Move ${kind} up`} icon="chevron-up" disabled={busy || index === 0} onPress={() => move(index, index - 1)} />
                  <Arrow label={`Move ${kind} down`} icon="chevron-down" disabled={busy || index === addresses.length - 1} onPress={() => move(index, index + 1)} />
                </View>
              ) : null}
            </View>
          );
        })}
      </View>
    </View>
  );
}
