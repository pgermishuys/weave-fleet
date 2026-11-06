import type { ReactNode } from "react";
import { ActivityIndicator, Pressable, TextInput, View } from "react-native";
import { useTheme } from "~/theme";
import { StatusGlyph } from "./glyph";
import { Icon } from "./icons";
import { Text } from "./text";

export function Composer({ value, onChangeText, onSend, placeholder = "Message", sending, working, machine, model, disabled, chip, sendDisabled, autoFocus }: {
  value: string; onChangeText: (text: string) => void; onSend: () => void; placeholder?: string;
  sending?: boolean; working?: boolean; machine?: string; model?: string; disabled?: boolean;
  /** Replaces the machine and model text in the toolbar: a tappable chip. */
  chip?: ReactNode;
  /** Send is off, but the box still takes text. */
  sendDisabled?: boolean;
  autoFocus?: boolean;
}) {
  const { color, font, radius } = useTheme();
  const canSend = !disabled && !sendDisabled && !sending && value.trim().length > 0;
  return (
    <View style={{
      borderWidth: 1, borderColor: color.tint9, borderRadius: radius.panel, backgroundColor: color.card,
      paddingHorizontal: 12, paddingTop: 8, paddingBottom: 6, gap: 2, opacity: disabled ? 0.5 : 1,
    }}>
      {working ? (
        <View style={{ flexDirection: "row", alignItems: "center", gap: 8, paddingTop: 2 }}>
          <StatusGlyph status="working" size={10} />
          <Text size={12.5} tone="muted">Working</Text>
        </View>
      ) : null}
      <TextInput
        testID="composer" value={value} onChangeText={onChangeText} placeholder={placeholder} placeholderTextColor={color.muted}
        editable={!disabled} multiline autoFocus={autoFocus}
        style={{ color: color.text, fontFamily: font.regular, fontSize: 16, minHeight: 40, maxHeight: 140, paddingTop: 8, paddingBottom: 6, textAlignVertical: "top" }}
      />
      <View style={{ flexDirection: "row", alignItems: "center", gap: 6 }}>
        <Pressable accessibilityRole="button" accessibilityLabel="Attach" style={{ width: 44, height: 44, marginLeft: -12, alignItems: "center", justifyContent: "center" }}>
          <Icon name="paperclip" size={18} color={color.muted} />
        </Pressable>
        <View style={{ flex: 1, flexDirection: "row", alignItems: "center", gap: 12 }}>
          {chip}
          {machine ? <Text size={12.5} tone="muted" numberOfLines={1} style={{ flexShrink: 1 }}>{machine}</Text> : null}
          {model ? <Text size={12.5} tone="muted" numberOfLines={1} style={{ flexShrink: 1 }}>{model}</Text> : null}
        </View>
        <Pressable
          testID="send" onPress={onSend} disabled={!canSend} accessibilityRole="button" accessibilityLabel="Send"
          style={{ width: 44, height: 44, marginRight: -6, alignItems: "center", justifyContent: "center" }}
        >
          <View style={{ width: 32, height: 32, borderRadius: 16, backgroundColor: color.accent, alignItems: "center", justifyContent: "center", opacity: canSend || sending ? 1 : 0.4 }}>
            {sending ? <ActivityIndicator size="small" color={color.onAccent} /> : <Icon name="send" size={17} color={color.onAccent} />}
          </View>
        </Pressable>
      </View>
    </View>
  );
}

export function UserBubble({ text, sentBy }: { text: string; sentBy?: string }) {
  const { color } = useTheme();
  return (
    <View style={{ alignSelf: "flex-end", maxWidth: "84%", gap: 3, alignItems: "flex-end" }}>
      {sentBy ? <Text size={12} tone="muted">Sent by {sentBy}</Text> : null}
      <View style={{ paddingHorizontal: 12, paddingVertical: 8, borderRadius: 14, backgroundColor: color.tint5, borderWidth: 1, borderColor: color.border }}>
        <Text selectable>{text}</Text>
      </View>
    </View>
  );
}
