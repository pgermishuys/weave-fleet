import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'

vi.mock('@/lib/api-client', () => ({
  apiFetch: vi.fn(),
}))

import { apiFetch } from '@/lib/api-client'
import { useSmartLinksStore } from '@/stores/smart-links'
import { useSessionsStore } from '@/stores/sessions'
import SmartLinksPanel from '../SmartLinksPanel.vue'

const mockApiFetch = vi.mocked(apiFetch)

describe('SmartLinksPanel', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    vi.resetAllMocks()
  })

  it('shows empty state when no session is active', () => {
    const wrapper = mount(SmartLinksPanel, { global: { plugins: [createPinia()] } })
    expect(wrapper.text()).toContain('Open a session')
  })

  it('shows empty state when session has no links', () => {
    const pinia = createPinia()
    setActivePinia(pinia)
    const sessionsStore = useSessionsStore()
    sessionsStore.setActiveSessionId('session-1')

    const wrapper = mount(SmartLinksPanel, { global: { plugins: [pinia] } })
    expect(wrapper.text()).toContain('No smart links detected')
  })

  it('renders active links for the current session', () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    const sessionsStore = useSessionsStore()
    sessionsStore.setActiveSessionId('session-1')

    const smartLinksStore = useSmartLinksStore()
    smartLinksStore.setLinks('session-1', [
      {
        id: 'link-1',
        sessionId: 'session-1',
        url: 'https://github.com/owner/repo/pull/1',
        providerId: 'github',
        resourceType: 'pull_request',
        resourceId: 'owner/repo#1',
        title: 'My Pull Request',
        status: 'open',
        statusLabel: 'Open',
        metadataJson: null,
        isDismissed: false,
        isTerminal: false,
        createdAt: '2024-01-01T00:00:00Z',
        updatedAt: '2024-01-01T00:00:00Z',
      },
    ])

    const wrapper = mount(SmartLinksPanel, { global: { plugins: [pinia] } })
    expect(wrapper.text()).toContain('My Pull Request')
  })

  it('does not render dismissed links', () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    const sessionsStore = useSessionsStore()
    sessionsStore.setActiveSessionId('session-1')

    const smartLinksStore = useSmartLinksStore()
    smartLinksStore.setLinks('session-1', [
      {
        id: 'link-1',
        sessionId: 'session-1',
        url: 'https://github.com/owner/repo/pull/1',
        providerId: 'github',
        resourceType: 'pull_request',
        resourceId: 'owner/repo#1',
        title: 'Dismissed PR',
        status: 'open',
        statusLabel: 'Open',
        metadataJson: null,
        isDismissed: true,
        isTerminal: false,
        createdAt: '2024-01-01T00:00:00Z',
        updatedAt: '2024-01-01T00:00:00Z',
      },
    ])

    const wrapper = mount(SmartLinksPanel, { global: { plugins: [pinia] } })
    expect(wrapper.text()).not.toContain('Dismissed PR')
    expect(wrapper.text()).toContain('No smart links detected')
  })

  it('calls dismiss API and updates store when dismiss is clicked', async () => {
    const pinia = createPinia()
    setActivePinia(pinia)

    const sessionsStore = useSessionsStore()
    sessionsStore.setActiveSessionId('session-1')

    const smartLinksStore = useSmartLinksStore()
    smartLinksStore.setLinks('session-1', [
      {
        id: 'link-1',
        sessionId: 'session-1',
        url: 'https://github.com/owner/repo/pull/1',
        providerId: 'github',
        resourceType: 'pull_request',
        resourceId: 'owner/repo#1',
        title: 'PR to dismiss',
        status: 'open',
        statusLabel: 'Open',
        metadataJson: null,
        isDismissed: false,
        isTerminal: false,
        createdAt: '2024-01-01T00:00:00Z',
        updatedAt: '2024-01-01T00:00:00Z',
      },
    ])

    mockApiFetch.mockResolvedValue({ ok: true } as Response)

    const wrapper = mount(SmartLinksPanel, { global: { plugins: [pinia] } })
    await wrapper.find('.smart-link-dismiss').trigger('click')
    await flushPromises()

    expect(mockApiFetch).toHaveBeenCalledWith(
      '/api/sessions/session-1/smart-links/link-1/dismiss',
      { method: 'PATCH' },
    )
    expect(smartLinksStore.isUrlDismissed('session-1', 'https://github.com/owner/repo/pull/1')).toBe(true)
  })
})
