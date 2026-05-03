import { useSmartLinkProviders } from './composables/use-smart-link-providers'
import { githubSmartLinkProvider } from './providers/github-smart-link-provider'

// Register providers eagerly when the module is first imported
const registry = useSmartLinkProviders()
registry.register(githubSmartLinkProvider)

export { useSmartLinks } from './composables/use-smart-links'
export { useSmartLinkProviders } from './composables/use-smart-link-providers'
export type { SmartLink, SmartLinkProvider, SmartLinkResolution } from './types'
