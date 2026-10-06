import { describe, expect, it } from 'vitest'
import { mermaidEntityCodes } from '@/lib/mermaid'

describe('mermaidEntityCodes', () => {
  it('turns HTML entities into Mermaid entity codes, whose ; does not end the statement', () => {
    expect(mermaidEntityCodes('participant C as IOptionsMonitorCache&lt;T&gt;'))
      .toBe('participant C as IOptionsMonitorCache#lt;T#gt;')
    expect(mermaidEntityCodes('A->>B: a &amp; b &#60;3')).toBe('A->>B: a #amp; b #60;3')
  })

  it('leaves everything else alone', () => {
    const source = 'sequenceDiagram\n  A->>B: one; two & three #lt; done<br/>next'
    expect(mermaidEntityCodes(source)).toBe(source)
  })
})
