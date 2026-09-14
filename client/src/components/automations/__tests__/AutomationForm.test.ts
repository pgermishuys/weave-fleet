import { describe, it, expect, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import AutomationForm from '../AutomationForm.vue'
import type { Automation } from '@/composables/use-automations'

// Mock the useAutomations composable
vi.mock('@/composables/use-automations', () => ({
  useAutomations: () => ({
    fetchEventCatalog: vi.fn().mockResolvedValue([]),
  }),
}))

describe('AutomationForm - Dirty State Tracking', () => {
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const initialAutomation: Partial<Automation> = {
    id: 'test-id',
    name: 'Test Automation',
    prompt: 'Test prompt',
    triggerType: 'schedule',
    triggerConfig: '0 0 * * *',
    targetType: 'new_session',
    maxConcurrentRuns: 1,
    maxRunsPerHour: 10,
    timeoutMinutes: 30,
  }

  it('keeps save button disabled when no changes are made in edit mode', async () => {
    const wrapper = mount(AutomationForm, {
      props: {
        mode: 'edit',
        initialValues: initialAutomation,
      },
    })

    await flushPromises()

    // Save button should be disabled when no changes are made
    const saveButton = wrapper.find('button[type="submit"]')
    expect(saveButton.attributes('disabled')).toBeDefined()
  })

  it('enables save button when name changes in edit mode', async () => {
    const wrapper = mount(AutomationForm, {
      props: {
        mode: 'edit',
        initialValues: initialAutomation,
      },
    })

    await flushPromises()

    // Change the name
    const nameInput = wrapper.find('#automation-name')
    await nameInput.setValue('Updated Name')
    await flushPromises()

    // Save button should now be enabled
    const saveButton = wrapper.find('button[type="submit"]')
    expect(saveButton.attributes('disabled')).toBeUndefined()
  })

  it('enables save button in create mode when validation passes', async () => {
    const wrapper = mount(AutomationForm, {
      props: {
        mode: 'create',
      },
    })

    await flushPromises()

    // Fill in required fields
    await wrapper.find('#automation-name').setValue('New Automation')
    await wrapper.find('#automation-prompt').setValue('Test prompt')
    await wrapper.find('#automation-trigger-config').setValue('0 0 * * *')
    await flushPromises()

    // Save button should be enabled in create mode when validation passes
    const saveButton = wrapper.find('button[type="submit"]')
    expect(saveButton.attributes('disabled')).toBeUndefined()
  })
})

describe('AutomationForm - what the server and the clock say', () => {
  it('shows why the server refused the last submit', async () => {
    const wrapper = mount(AutomationForm, {
      props: { mode: 'create', submitError: 'Invalid cron expression: bad format' },
    })
    await flushPromises()

    expect(wrapper.find('[role="alert"]').text()).toContain('Invalid cron expression: bad format')
  })

  it('warns that saving an older UTC schedule moves it into this browser\'s zone', async () => {
    const wrapper = mount(AutomationForm, {
      props: {
        mode: 'edit',
        initialValues: { id: 'old', name: 'Old', prompt: 'p', triggerType: 'schedule', triggerConfig: '0 9 * * 1', timeZone: null },
      },
    })
    await flushPromises()

    const zone = Intl.DateTimeFormat().resolvedOptions().timeZone
    const note = wrapper.find('[data-testid="automation-time-zone-change"]')
    if (zone === 'UTC') {
      expect(note.exists()).toBe(false)
    } else {
      expect(note.text()).toBe(`Saved in UTC. Saving changes it to ${zone}.`)
    }
  })

  it('sends the browser\'s time zone with a schedule', async () => {
    const wrapper = mount(AutomationForm, { props: { mode: 'create' } })
    await flushPromises()

    await wrapper.find('#automation-name').setValue('Weekly digest')
    await wrapper.find('#automation-prompt').setValue('Summarise the open PRs')
    await wrapper.find('#automation-trigger-config').setValue('0 9 * * 1')
    await wrapper.find('form').trigger('submit')

    const [[request]] = wrapper.emitted('submit') as [[{ timeZone: string | null }]]
    expect(request.timeZone).toBe(Intl.DateTimeFormat().resolvedOptions().timeZone)
  })
})
