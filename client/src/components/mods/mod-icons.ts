import {
  ArrowRight, Bug, Check, ChevronRight, Circle, Clock, Copy, Dot, ExternalLink, Eye, EyeOff, File,
  FlaskConical, Folder, Gauge, GitBranch, Info, LoaderCircle, Play, Search, SkipForward, Sparkles, Terminal, TriangleAlert, X, Zap,
} from "lucide-vue-next";
import type { Component } from "vue";
import type { ModIconName } from "@/lib/mods/types";

/** The icons a mod can name, as Fleet's own (lucide) set. */
export const MOD_ICONS: Readonly<Record<ModIconName, Component>> = {
  check: Check,
  x: X,
  alert: TriangleAlert,
  info: Info,
  circle: Circle,
  dot: Dot,
  clock: Clock,
  loader: LoaderCircle,
  play: Play,
  skip: SkipForward,
  test: FlaskConical,
  bug: Bug,
  terminal: Terminal,
  file: File,
  folder: Folder,
  "git-branch": GitBranch,
  search: Search,
  sparkles: Sparkles,
  zap: Zap,
  gauge: Gauge,
  "arrow-right": ArrowRight,
  "chevron-right": ChevronRight,
  "external-link": ExternalLink,
  copy: Copy,
  eye: Eye,
  "eye-off": EyeOff,
};

export function modIcon(name: unknown): Component | undefined {
  return typeof name === "string" ? (MOD_ICONS as Record<string, Component>)[name] : undefined;
}
