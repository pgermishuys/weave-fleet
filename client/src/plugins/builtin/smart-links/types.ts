/**
 * A URL detected in a session message, enriched with live status from an external provider.
 * Mirrors the SmartLink domain entity on the backend.
 */
export interface SmartLink {
  id: string
  sessionId: string
  url: string
  providerId: string
  resourceType: string
  resourceId: string
  title: string
  status: string
  statusLabel: string
  metadata: Record<string, unknown>
  isDismissed: boolean
  isTerminal: boolean
  createdAt: string
  updatedAt: string
}

/**
 * The resolved information returned by a provider when it successfully handles a URL.
 */
export interface SmartLinkResolution {
  providerId: string
  resourceType: string
  resourceId: string
  title: string
  status: string
  statusLabel: string
  isTerminal: boolean
  metadata: Record<string, unknown>
}

/**
 * A provider that can detect and resolve URLs into enriched SmartLink metadata.
 * Providers register themselves with the SmartLink provider registry.
 */
export interface SmartLinkProvider {
  /** Unique identifier for this provider (e.g. "github", "linear"). */
  id: string
  /** Returns true if this provider can handle the given URL. */
  canHandle(url: string): boolean
  /** Resolves the URL into enriched metadata. Returns null if resolution fails. */
  resolve(url: string): Promise<SmartLinkResolution | null>
}
