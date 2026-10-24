import { Image } from "react-native";
import Svg, { Path } from "react-native-svg";
import { useTheme } from "~/theme";

// Lucide's paths (24 grid, 2px round stroke), the same icon family as desktop Fleet.
const PATHS = {
  back: ["m15 18-6-6 6-6"],
  "chevron-right": ["m9 18 6-6-6-6"],
  "chevron-down": ["m6 9 6 6 6-6"],
  "chevron-up": ["m18 15-6-6-6 6"],
  plus: ["M5 12h14", "M12 5v14"],
  bell: ["M10.268 21a2 2 0 0 0 3.464 0", "M3.262 15.326A1 1 0 0 0 4 17h16a1 1 0 0 0 .74-1.673C19.41 13.956 18 12.499 18 8A6 6 0 0 0 6 8c0 4.499-1.411 5.956-2.738 7.326"],
  shield: ["M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z"],
  sessions: ["M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"],
  workflows: ["M3 5a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z", "M7 11v4a2 2 0 0 0 2 2h4", "M13 15a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2h-4a2 2 0 0 1-2-2z"],
  automations: ["M4 14a1 1 0 0 1-.78-1.63l9.9-10.2a.5.5 0 0 1 .86.46l-1.92 6.02A1 1 0 0 0 13 10h7a1 1 0 0 1 .78 1.63l-9.9 10.2a.5.5 0 0 1-.86-.46l1.92-6.02A1 1 0 0 0 11 14z"],
  machines: ["M4 2h16a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2z", "M4 14h16a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2v-4a2 2 0 0 1 2-2z", "M6 6h.01", "M6 18h.01"],
  file: ["M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7z", "M14 2v4a2 2 0 0 0 2 2h4"],
  edit: ["M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z", "m15 5 4 4"],
  terminal: ["m4 17 6-6-6-6", "M12 19h8"],
  search: ["m21 21-4.34-4.34", "M19 11a8 8 0 1 1-16 0 8 8 0 0 1 16 0z"],
  send: ["m5 12 7-7 7 7", "M12 19V5"],
  paperclip: ["m16 6-8.414 8.586a2 2 0 0 0 2.829 2.829l8.414-8.586a4 4 0 1 0-5.657-5.657l-8.379 8.551a6 6 0 1 0 8.485 8.485l8.379-8.551"],
  more: ["M12 12h.01", "M19 12h.01", "M5 12h.01"],
  check: ["M20 6 9 17l-5-5"],
  x: ["M18 6 6 18", "m6 6 12 12"],
  branch: ["M6 3v12", "M21 6a3 3 0 1 1-6 0 3 3 0 0 1 6 0z", "M9 18a3 3 0 1 1-6 0 3 3 0 0 1 6 0z", "M18 9a9 9 0 0 1-9 9"],
  pr: ["M21 18a3 3 0 1 1-6 0 3 3 0 0 1 6 0z", "M9 6a3 3 0 1 1-6 0 3 3 0 0 1 6 0z", "M13 6h3a2 2 0 0 1 2 2v7", "M6 9v12"],
  qr: ["M3 3h6v6H3z", "M15 3h6v6h-6z", "M3 15h6v6H3z", "M15 15h2v2h-2z", "M21 15v.01", "M15 21h2", "M21 19v2"],
  trash: ["M3 6h18", "M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6", "M8 6V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"],
  eye: ["M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0", "M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0z"],
  globe: ["M22 12a10 10 0 1 1-20 0 10 10 0 0 1 20 0z", "M2 12h20", "M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"],
  page: ["M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z", "M10 4v4", "M2 8h20", "M6 4v4"],
  pick: ["M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z", "m9 12 2 2 4-4"],
  "external-link": ["M15 3h6v6", "M10 14 21 3", "M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"],
  reload: ["M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8", "M21 3v5h-5", "M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16", "M8 16H3v5"],
} as const;

export type IconName = keyof typeof PATHS;
export const ICON_NAMES = Object.keys(PATHS) as IconName[];

export function Icon({ name, size = 18, color }: { name: IconName; size?: number; color?: string }) {
  const theme = useTheme();
  return (
    <Svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke={color ?? theme.color.text}
      strokeWidth={name === "more" ? 3 : 2} strokeLinecap="round" strokeLinejoin="round">
      {PATHS[name].map((d) => <Path key={d} d={d} />)}
    </Svg>
  );
}

/** Fleet's logo, for the Sessions header. */
export function FleetMark({ height = 24 }: { height?: number }) {
  return <Image source={require("../../assets/fleet-mark.png")} style={{ height, width: (height * 511) / 234 }} accessibilityLabel="Fleet" />;
}
