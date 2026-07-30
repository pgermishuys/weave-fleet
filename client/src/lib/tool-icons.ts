import type { Component } from 'vue'
import {
  FileText,
  Pencil,
  Search,
  Layers,
  Terminal,
  GitBranch,
  Globe,
  MessageCircleQuestion,
  Wrench,
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
