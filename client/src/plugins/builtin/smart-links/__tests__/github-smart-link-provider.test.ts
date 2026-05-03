import { describe, it, expect, vi, beforeEach } from 'vitest'
import { githubSmartLinkProvider } from '../providers/github-smart-link-provider'

// Mock apiFetch
vi.mock('@/lib/api-client', () => ({
  apiFetch: vi.fn(),
}))

import { apiFetch } from '@/lib/api-client'

const mockApiFetch = vi.mocked(apiFetch)

describe('githubSmartLinkProvider', () => {
  beforeEach(() => {
    vi.resetAllMocks()
  })

  describe('canHandle', () => {
    it('returns true for GitHub PR URLs', () => {
      expect(githubSmartLinkProvider.canHandle('https://github.com/owner/repo/pull/123')).toBe(true)
    })

    it('returns true for GitHub issue URLs', () => {
      expect(githubSmartLinkProvider.canHandle('https://github.com/owner/repo/issues/42')).toBe(true)
    })

    it('returns false for non-GitHub URLs', () => {
      expect(githubSmartLinkProvider.canHandle('https://linear.app/org/issue/ENG-123')).toBe(false)
    })

    it('returns false for GitHub repo root URLs', () => {
      expect(githubSmartLinkProvider.canHandle('https://github.com/owner/repo')).toBe(false)
    })

    it('returns false for GitHub commit URLs', () => {
      expect(githubSmartLinkProvider.canHandle('https://github.com/owner/repo/commit/abc123')).toBe(false)
    })

    it('returns true for PR URLs with query params', () => {
      expect(githubSmartLinkProvider.canHandle('https://github.com/owner/repo/pull/1?diff=split')).toBe(true)
    })
  })

  describe('resolve - pull requests', () => {
    it('resolves an open PR correctly', async () => {
      mockApiFetch.mockResolvedValue({
        ok: true,
        json: async () => ({ number: 123, title: 'My PR', state: 'open', merged: false, draft: false, html_url: '' }),
      } as Response)

      const result = await githubSmartLinkProvider.resolve('https://github.com/owner/repo/pull/123')

      expect(result).toMatchObject({
        providerId: 'github',
        resourceType: 'pull_request',
        resourceId: 'owner/repo#123',
        status: 'open',
        statusLabel: 'Open',
        isTerminal: false,
      })
      expect(result?.title).toContain('My PR')
    })

    it('resolves a merged PR as terminal', async () => {
      mockApiFetch.mockResolvedValue({
        ok: true,
        json: async () => ({ number: 123, title: 'Merged PR', state: 'closed', merged: true, draft: false, html_url: '' }),
      } as Response)

      const result = await githubSmartLinkProvider.resolve('https://github.com/owner/repo/pull/123')

      expect(result?.status).toBe('merged')
      expect(result?.statusLabel).toBe('Merged')
      expect(result?.isTerminal).toBe(true)
    })

    it('resolves a closed (not merged) PR as terminal', async () => {
      mockApiFetch.mockResolvedValue({
        ok: true,
        json: async () => ({ number: 123, title: 'Closed PR', state: 'closed', merged: false, draft: false, html_url: '' }),
      } as Response)

      const result = await githubSmartLinkProvider.resolve('https://github.com/owner/repo/pull/123')

      expect(result?.status).toBe('closed')
      expect(result?.isTerminal).toBe(true)
    })

    it('resolves a draft PR as non-terminal', async () => {
      mockApiFetch.mockResolvedValue({
        ok: true,
        json: async () => ({ number: 123, title: 'Draft PR', state: 'open', merged: false, draft: true, html_url: '' }),
      } as Response)

      const result = await githubSmartLinkProvider.resolve('https://github.com/owner/repo/pull/123')

      expect(result?.status).toBe('draft')
      expect(result?.isTerminal).toBe(false)
    })

    it('returns null when API call fails', async () => {
      mockApiFetch.mockResolvedValue({ ok: false } as Response)

      const result = await githubSmartLinkProvider.resolve('https://github.com/owner/repo/pull/123')

      expect(result).toBeNull()
    })

    it('returns null when fetch throws', async () => {
      mockApiFetch.mockRejectedValue(new Error('Network error'))

      const result = await githubSmartLinkProvider.resolve('https://github.com/owner/repo/pull/123')

      expect(result).toBeNull()
    })
  })

  describe('resolve - issues', () => {
    it('resolves an open issue correctly', async () => {
      mockApiFetch.mockResolvedValue({
        ok: true,
        json: async () => ({ number: 42, title: 'Bug report', state: 'open', html_url: '' }),
      } as Response)

      const result = await githubSmartLinkProvider.resolve('https://github.com/owner/repo/issues/42')

      expect(result).toMatchObject({
        providerId: 'github',
        resourceType: 'issue',
        resourceId: 'owner/repo#42',
        status: 'open',
        statusLabel: 'Open',
        isTerminal: false,
      })
    })

    it('resolves a closed issue as terminal', async () => {
      mockApiFetch.mockResolvedValue({
        ok: true,
        json: async () => ({ number: 42, title: 'Fixed bug', state: 'closed', html_url: '' }),
      } as Response)

      const result = await githubSmartLinkProvider.resolve('https://github.com/owner/repo/issues/42')

      expect(result?.status).toBe('closed')
      expect(result?.isTerminal).toBe(true)
    })

    it('calls correct API endpoint for issues', async () => {
      mockApiFetch.mockResolvedValue({
        ok: true,
        json: async () => ({ number: 7, title: 'Issue', state: 'open', html_url: '' }),
      } as Response)

      await githubSmartLinkProvider.resolve('https://github.com/acme/widget/issues/7')

      expect(mockApiFetch).toHaveBeenCalledWith(
        '/api/integrations/github/repos/acme/widget/issues/7',
      )
    })
  })
})
