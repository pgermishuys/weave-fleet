import type { FileDiffItem } from '@/api/client'

export interface ArtifactTreeNode {
  name: string
  fullPath: string
  children: ArtifactTreeNode[]
  isDirectory: boolean
  status?: 'added' | 'deleted' | 'modified'
  additions?: number
  deletions?: number
}

interface TreeMap {
  [key: string]: TreeMapNode
}

interface TreeMapNode {
  name: string
  fullPath: string
  children: TreeMap
  isDirectory: boolean
  status?: 'added' | 'deleted' | 'modified'
  additions?: number
  deletions?: number
}

/**
 * Builds a nested tree structure from an array of file diffs.
 * Directories are sorted before files, both alphabetically.
 * Single-child directory chains are collapsed (e.g., src/components/).
 */
export function buildArtifactTree(diffs: FileDiffItem[]): ArtifactTreeNode[] {
  const root: TreeMap = {}

  // Build the initial tree structure using nested maps
  for (const diff of diffs) {
    const parts = diff.file.split('/')
    let currentLevel = root

    for (let i = 0; i < parts.length; i++) {
      const part = parts[i]
      const isLastPart = i === parts.length - 1
      const fullPath = parts.slice(0, i + 1).join('/')

      if (!currentLevel[part]) {
        currentLevel[part] = {
          name: part,
          fullPath,
          children: {},
          isDirectory: !isLastPart,
          ...(isLastPart && {
            status: diff.status,
            additions: diff.additions,
            deletions: diff.deletions,
          }),
        }
      }

      if (!isLastPart) {
        currentLevel = currentLevel[part].children
      }
    }
  }

  // Convert maps to arrays recursively
  const convertToArray = (treeMap: TreeMap): ArtifactTreeNode[] => {
    return Object.values(treeMap).map(node => ({
      name: node.name,
      fullPath: node.fullPath,
      children: convertToArray(node.children),
      isDirectory: node.isDirectory,
      ...(node.status && { status: node.status }),
      ...(node.additions !== undefined && { additions: node.additions }),
      ...(node.deletions !== undefined && { deletions: node.deletions }),
    }))
  }

  let result = convertToArray(root)
  result = sortNodes(result)

  // Collapse single-child directory chains
  result = result.map(node => collapseSingleChildDirs(node))

  return result
}

/**
 * Recursively collapses single-child directory chains.
 * If a directory has only one child and that child is also a directory,
 * combine their names (e.g., src/ + components/ = src/components/).
 */
function collapseSingleChildDirs(node: ArtifactTreeNode): ArtifactTreeNode {
  if (!node.isDirectory) {
    return node
  }

  let current = node
  const nameParts: string[] = [current.name]

  // Keep collapsing while we have exactly one child that is a directory
  while (
    current.children.length === 1 &&
    current.children[0].isDirectory
  ) {
    current = current.children[0]
    nameParts.push(current.name)
  }

  // If we collapsed anything, create a new node with combined name
  if (nameParts.length > 1) {
    return {
      name: nameParts.join('/'),
      fullPath: current.fullPath,
      children: sortNodes(current.children.map(child => collapseSingleChildDirs(child))),
      isDirectory: true,
    }
  }

  // Otherwise, just recursively process children
  return {
    ...node,
    children: sortNodes(node.children.map(child => collapseSingleChildDirs(child))),
  }
}

/**
 * Sorts nodes: directories first, then files, both alphabetically.
 */
function sortNodes(nodes: ArtifactTreeNode[]): ArtifactTreeNode[] {
  return nodes.sort((a, b) => {
    // Directories before files
    if (a.isDirectory && !b.isDirectory) return -1
    if (!a.isDirectory && b.isDirectory) return 1

    // Alphabetical within same type
    return a.name.localeCompare(b.name)
  })
}
