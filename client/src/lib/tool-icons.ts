import type { Component } from 'vue'
import {
  AppWindow,
  BookOpen,
  Camera,
  CircleCheck,
  FileText,
  Pencil,
  Search,
  Layers,
  MessagesSquare,
  MonitorUp,
  Server,
  Terminal,
  GitBranch,
  Globe,
  Lightbulb,
  LightbulbOff,
  MessageCircleQuestion,
  PanelsTopLeft,
  Send,
  Wrench,
  Code,
} from 'lucide-vue-next'

const iconMap: Record<string, Component> = {
  read: FileText,
  write: Pencil,
  edit: Pencil,
  glob: Search,
  grep: Search,
  skill: Layers,
  bash: Terminal,
  task: GitBranch,
  webfetch: Globe,
  question: MessageCircleQuestion,
  fleet_app_start: AppWindow,
  fleet_browser_open: Globe,
  fleet_browser_screenshot: Camera,
  fleet_browser_read: Globe,
  fleet_browser_act: Globe,
  fleet_page_show: PanelsTopLeft,
  fleet_walkthrough_show: BookOpen,
  fleet_message: Send,
  fleet_session_read: MessagesSquare,
  fleet_machine_list: Server,
  fleet_session_start: MonitorUp,
  fleet_memory_save: Lightbulb,
  fleet_memory_forget: LightbulbOff,
  fleet_step_done: CircleCheck,
  // OpenCode 2's names for its tools.
  shell: Terminal,
  subagent: GitBranch,
  websearch: Search,
  execute: Code,
}

const labelMap: Record<string, string> = {
  read: 'Read',
  write: 'Write',
  edit: 'Edit',
  glob: 'Glob',
  grep: 'Grep',
  skill: 'Skill',
  bash: 'Bash',
  task: 'Task',
  webfetch: 'Web Fetch',
  question: 'Question',
  fleet_app_start: 'Run app',
  fleet_browser_open: 'Open page',
  fleet_browser_screenshot: 'Screenshot',
  fleet_browser_read: 'Read page',
  fleet_browser_act: 'Use page',
  fleet_page_show: 'Show page',
  fleet_walkthrough_show: 'Walkthrough',
  fleet_message: 'Message session',
  fleet_session_read: 'Read session',
  fleet_machine_list: 'List machines',
  fleet_session_start: 'Start session on a machine',
  fleet_memory_save: 'Remember',
  fleet_memory_forget: 'Forget note',
  fleet_step_done: 'Step done',
  // OpenCode 2's names for its tools.
  shell: 'Shell',
  subagent: 'Subagent',
  websearch: 'Web Search',
  execute: 'Code',
}

export function getToolIcon(kind: string): Component {
  return iconMap[kind] ?? Wrench
}

export function getToolDisplayLabel(kind: string): string {
  if (labelMap[kind]) {
    return labelMap[kind]
  }
  // Title case fallback: capitalize first letter
  return kind.charAt(0).toUpperCase() + kind.slice(1)
}
