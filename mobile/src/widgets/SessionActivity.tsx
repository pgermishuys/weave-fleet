// The iOS Live Activity for a session: lock screen banner and Dynamic Island. expo-widgets compiles this into the
// widget extension; the 'widget' directive means only @expo/ui/swift-ui components and nothing from outside the
// function (so the colours are written out here).
import { HStack, Image, Text, VStack } from "@expo/ui/swift-ui";
import { font, foregroundStyle, padding } from "@expo/ui/swift-ui/modifiers";
import { createLiveActivity, type LiveActivityEnvironment } from "expo-widgets";

export interface SessionActivityProps {
  title: string;
  /** "Working", "Needs you", "Finished". */
  state: string;
  /** One line: the step running, or the ask. */
  detail: string;
  /** Unix ms when the turn started, for the timer. */
  startedAt: number;
  needsYou: boolean;
}

const SessionActivity = (props: SessionActivityProps, _environment: LiveActivityEnvironment) => {
  "widget";
  const tint = props.needsYou ? "#f59e0b" : "#22c55e";
  const symbol = props.needsYou ? "exclamationmark.shield" : "circle.grid.2x2.fill";
  return {
    banner: (
      <VStack alignment="leading" spacing={4} modifiers={[padding({ all: 14 })]}>
        <HStack spacing={6}>
          <Image systemName={symbol} color={tint} />
          <Text modifiers={[font({ weight: "semibold", size: 13 }), foregroundStyle(tint)]}>{props.state}</Text>
        </HStack>
        <Text modifiers={[font({ weight: "semibold", size: 16 })]}>{props.title}</Text>
        <Text modifiers={[font({ size: 13 }), foregroundStyle("#8e8e9a")]}>{props.detail}</Text>
      </VStack>
    ),
    compactLeading: <Image systemName={symbol} color={tint} />,
    compactTrailing: <Text modifiers={[font({ size: 12 }), foregroundStyle(tint)]}>{props.needsYou ? "Needs you" : "Working"}</Text>,
    minimal: <Image systemName={symbol} color={tint} />,
    expandedLeading: <Image systemName={symbol} color={tint} />,
    expandedCenter: <Text modifiers={[font({ weight: "semibold", size: 15 })]}>{props.title}</Text>,
    expandedBottom: <Text modifiers={[font({ size: 13 }), foregroundStyle("#8e8e9a")]}>{`${props.state} · ${props.detail}`}</Text>,
  };
};

export default createLiveActivity<SessionActivityProps>("SessionActivity", SessionActivity);
