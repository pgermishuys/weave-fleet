import { Pressable, View } from "react-native";
import { useTheme } from "~/theme";
import { Icon } from "./icons";
import { Text } from "./text";

/** "atlas joined your Fleet" at the top of the sessions list (Your Fleet). Tap to see the machine; the cross dismisses it. */
export function JoinedBanner({ text, onPress, onDismiss, testID }: { text: string; onPress: () => void; onDismiss: () => void; testID?: string }) {
  const { color, radius } = useTheme();
  return (
    <View testID={testID} style={{ flexDirection: "row", alignItems: "center", gap: 4, marginHorizontal: 12, marginBottom: 8, paddingLeft: 12, minHeight: 48, borderRadius: radius.card, borderWidth: 1, borderColor: color.border, backgroundColor: color.accentDim }}>
      <Pressable onPress={onPress} accessibilityRole="button" accessibilityLabel={text} style={{ flex: 1, flexDirection: "row", alignItems: "center", gap: 8, minHeight: 48 }}>
        <Icon name="machines" size={16} color={color.accentText} />
        <Text size={13.5} style={{ flex: 1 }}>{text}</Text>
      </Pressable>
      <Pressable onPress={onDismiss} accessibilityRole="button" accessibilityLabel="Dismiss" hitSlop={8} style={{ width: 44, height: 48, alignItems: "center", justifyContent: "center" }}>
        <Icon name="x" size={16} color={color.muted} />
      </Pressable>
    </View>
  );
}
