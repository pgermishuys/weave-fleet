import { useEffect, useRef, useState } from "react";
import { AccessibilityInfo, Animated, Platform, View } from "react-native";
import Svg, { Circle, Polygon } from "react-native-svg";
import { useTheme } from "~/theme";

export type GlyphStatus = "working" | "retry" | "needs-you" | "idle" | "error" | "done" | "running";

const LABELS: Record<GlyphStatus, string> = {
  working: "Working", retry: "Retrying", "needs-you": "Needs you", idle: "Idle", error: "Error", done: "Done", running: "Running",
};

/** A session list item's activityStatus and sessionStatus (what the desktop row draws from) as the glyph to draw. */
export function glyphStatusOf(activityStatus?: string | null, sessionStatus?: string | null): GlyphStatus {
  if (sessionStatus === "waiting_input" || activityStatus === "waiting_input") return "needs-you";
  if (sessionStatus === "error") return "error";
  if (sessionStatus === "active") return activityStatus === "retry" ? "retry" : "working";
  if (sessionStatus === "completed") return "done";
  return "idle";
}

// One 200ms clock for every working glyph on screen: the missing dot steps clockwise, as on desktop.
let step = 0;
let timer: ReturnType<typeof setInterval> | undefined;
const listeners = new Set<(s: number) => void>();

function useQuadStep(): number {
  const [s, setS] = useState(step);
  useEffect(() => {
    let reduced = false;
    AccessibilityInfo.isReduceMotionEnabled().then((v) => { reduced = v; }).catch(() => {});
    const on = (next: number) => setS(reduced ? 0 : next);
    listeners.add(on);
    timer ??= setInterval(() => { step = (step + 1) % 4; listeners.forEach((l) => l(step)); }, 200);
    return () => {
      listeners.delete(on);
      if (!listeners.size && timer) { clearInterval(timer); timer = undefined; }
    };
  }, []);
  return s;
}

function Quad({ size, color }: { size: number; color: string }) {
  const dim = useQuadStep();
  const dot = (size * 3) / 8;
  // Row-major cells hold clockwise slots 0, 1, 3, 2.
  const slots = [0, 1, 3, 2];
  return (
    <View style={{ width: size, height: size, flexDirection: "row", flexWrap: "wrap", gap: size / 4 }}>
      {slots.map((slot) => (
        <View key={slot} style={{ width: dot, height: dot, borderRadius: dot / 2, backgroundColor: color, opacity: slot === dim ? 0.16 : 1 }} />
      ))}
    </View>
  );
}

function Pulse({ children }: { children: React.ReactNode }) {
  const opacity = useRef(new Animated.Value(1)).current;
  useEffect(() => {
    const native = Platform.OS !== "web";
    const loop = Animated.loop(Animated.sequence([
      Animated.timing(opacity, { toValue: 0.3, duration: 600, useNativeDriver: native }),
      Animated.timing(opacity, { toValue: 1, duration: 600, useNativeDriver: native }),
    ]));
    loop.start();
    return () => loop.stop();
  }, [opacity]);
  return <Animated.View style={{ opacity }}>{children}</Animated.View>;
}

export function StatusGlyph({ status, size = 10 }: { status: GlyphStatus; size?: number }) {
  const { color } = useTheme();
  const svg = (shape: React.ReactNode) => <Svg width={size} height={size} viewBox="0 0 8 8" fill="none">{shape}</Svg>;
  let body: React.ReactNode;
  switch (status) {
    case "working": body = <Quad size={size} color={color.text} />; break;
    case "retry": body = <Quad size={size} color={color.waiting} />; break;
    case "needs-you": body = svg(<Polygon points="4,0.5 7.5,4 4,7.5 0.5,4" fill={color.waiting} />); break;
    case "idle": body = svg(<Circle cx="4" cy="4" r="3" stroke={color.muted} strokeWidth="1.5" />); break;
    case "error": body = svg(<Polygon points="4,0.5 7.5,7.5 0.5,7.5" fill={color.error} />); break;
    case "done": body = svg(<Circle cx="4" cy="4" r="3.5" fill={color.complete} />); break;
    case "running": body = <Pulse>{svg(<Circle cx="4" cy="4" r="4" fill={color.running} />)}</Pulse>; break;
  }
  return <View accessible accessibilityRole="image" accessibilityLabel={LABELS[status]}>{body}</View>;
}
