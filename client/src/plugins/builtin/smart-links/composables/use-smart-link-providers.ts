import type { SmartLinkProvider } from '../types'

// Module-level singleton registry — providers registered at app startup
const _providers: SmartLinkProvider[] = []

export interface UseSmartLinkProviders {
  register(provider: SmartLinkProvider): void
  findProvider(url: string): SmartLinkProvider | null
  getAll(): SmartLinkProvider[]
}

/**
 * Returns a composable that holds registered SmartLinkProvider instances.
 * Uses a module-level singleton so providers are shared across the app.
 */
export function useSmartLinkProviders(): UseSmartLinkProviders {
  function register(provider: SmartLinkProvider): void {
    const existing = _providers.findIndex((p) => p.id === provider.id)
    if (existing >= 0) {
      _providers[existing] = provider
    } else {
      _providers.push(provider)
    }
  }

  function findProvider(url: string): SmartLinkProvider | null {
    return _providers.find((p) => p.canHandle(url)) ?? null
  }

  function getAll(): SmartLinkProvider[] {
    return [..._providers]
  }

  return { register, findProvider, getAll }
}
