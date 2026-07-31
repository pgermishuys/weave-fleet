import { describe, it, expect } from 'vitest'
import { parseVisualPayload } from '../visual-payload'

describe('parseVisualPayload', () => {
  describe('valid payloads', () => {
    it('parses visual/sequence payload', () => {
      const raw = JSON.stringify({
        $type: 'visual/sequence',
        content: 'some content',
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'visual/sequence',
        content: 'some content',
      })
    })

    it('parses html payload', () => {
      const raw = JSON.stringify({
        $type: 'html',
        content: '<div>Hello</div>',
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'html',
        content: '<div>Hello</div>',
      })
    })

    it('parses markdown payload', () => {
      const raw = JSON.stringify({
        $type: 'markdown',
        content: '# Title\n\nContent',
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'markdown',
        content: '# Title\n\nContent',
      })
    })

    it('parses visual/flow payload with string content', () => {
      const raw = JSON.stringify({
        $type: 'visual/flow',
        content: 'A -> B -> C',
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'visual/flow',
        content: 'A -> B -> C',
      })
    })

    it('parses visual/flow payload with object content', () => {
      const raw = JSON.stringify({
        $type: 'visual/flow',
        content: {
          nodes: ['A', 'B', 'C'],
          edges: [['A', 'B'], ['B', 'C']],
        },
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'visual/flow',
        content: {
          nodes: ['A', 'B', 'C'],
          edges: [['A', 'B'], ['B', 'C']],
        },
      })
    })

    it('parses visual/flow payload with object content and title', () => {
      const raw = JSON.stringify({
        $type: 'visual/flow',
        content: { diagram: 'data' },
        title: 'Flow Diagram',
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'visual/flow',
        content: { diagram: 'data' },
        title: 'Flow Diagram',
      })
    })

    it('parses payload with optional title', () => {
      const raw = JSON.stringify({
        $type: 'html',
        content: '<p>Test</p>',
        title: 'My Title',
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'html',
        content: '<p>Test</p>',
        title: 'My Title',
      })
    })

    it('ignores extra fields', () => {
      const raw = JSON.stringify({
        $type: 'markdown',
        content: 'test',
        extra: 'ignored',
        another: 123,
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'markdown',
        content: 'test',
      })
    })
  })

  describe('invalid payloads', () => {
    it('returns null for plain text', () => {
      const result = parseVisualPayload('just plain text')
      expect(result).toBeNull()
    })

    it('returns null for invalid JSON', () => {
      const result = parseVisualPayload('{ invalid json }')
      expect(result).toBeNull()
    })

    it('returns null for unknown $type', () => {
      const raw = JSON.stringify({
        $type: 'unknown',
        content: 'test',
      })
      const result = parseVisualPayload(raw)
      expect(result).toBeNull()
    })

    it('returns null for missing $type', () => {
      const raw = JSON.stringify({
        content: 'test',
      })
      const result = parseVisualPayload(raw)
      expect(result).toBeNull()
    })

    it('returns null for missing content', () => {
      const raw = JSON.stringify({
        $type: 'html',
      })
      const result = parseVisualPayload(raw)
      expect(result).toBeNull()
    })

    it('returns null for non-string content', () => {
      const raw = JSON.stringify({
        $type: 'html',
        content: 123,
      })
      const result = parseVisualPayload(raw)
      expect(result).toBeNull()
    })

    it('returns null for visual/flow with number content', () => {
      const raw = JSON.stringify({
        $type: 'visual/flow',
        content: 123,
      })
      const result = parseVisualPayload(raw)
      expect(result).toBeNull()
    })

    it('returns null for visual/flow with null content', () => {
      const raw = JSON.stringify({
        $type: 'visual/flow',
        content: null,
      })
      const result = parseVisualPayload(raw)
      expect(result).toBeNull()
    })

    it('returns null for visual/flow with array content', () => {
      const raw = JSON.stringify({
        $type: 'visual/flow',
        content: ['A', 'B', 'C'],
      })
      const result = parseVisualPayload(raw)
      expect(result).toBeNull()
    })

    it('returns null for non-string $type', () => {
      const raw = JSON.stringify({
        $type: 123,
        content: 'test',
      })
      const result = parseVisualPayload(raw)
      expect(result).toBeNull()
    })

    it('returns null for JSON array', () => {
      const raw = JSON.stringify([{ $type: 'html', content: 'test' }])
      const result = parseVisualPayload(raw)
      expect(result).toBeNull()
    })

    it('returns null for JSON primitive', () => {
      expect(parseVisualPayload('"string"')).toBeNull()
      expect(parseVisualPayload('123')).toBeNull()
      expect(parseVisualPayload('true')).toBeNull()
      expect(parseVisualPayload('null')).toBeNull()
    })
  })

  describe('security: prototype pollution prevention', () => {
    it('strips __proto__ key from top level', () => {
      const raw = JSON.stringify({
        $type: 'html',
        content: 'test',
        __proto__: { polluted: true },
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'html',
        content: 'test',
      })
      expect(result).not.toHaveProperty('__proto__')
    })

    it('strips constructor key from top level', () => {
      const raw = JSON.stringify({
        $type: 'html',
        content: 'test',
        constructor: { polluted: true },
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'html',
        content: 'test',
      })
      expect(result).not.toHaveProperty('constructor')
    })

    it('strips __proto__ recursively in nested objects', () => {
      const raw = JSON.stringify({
        $type: 'html',
        content: 'test',
        nested: {
          __proto__: { polluted: true },
          safe: 'value',
        },
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'html',
        content: 'test',
      })
    })

    it('strips constructor recursively in nested objects', () => {
      const raw = JSON.stringify({
        $type: 'html',
        content: 'test',
        nested: {
          constructor: { polluted: true },
          safe: 'value',
        },
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'html',
        content: 'test',
      })
    })

    it('strips dangerous keys in arrays', () => {
      const raw = JSON.stringify({
        $type: 'html',
        content: 'test',
        items: [
          { __proto__: { bad: true }, value: 1 },
          { constructor: { bad: true }, value: 2 },
        ],
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'html',
        content: 'test',
      })
    })

    it('handles deeply nested dangerous keys', () => {
      const raw = JSON.stringify({
        $type: 'html',
        content: 'test',
        level1: {
          level2: {
            level3: {
              __proto__: { polluted: true },
              constructor: { polluted: true },
              safe: 'value',
            },
          },
        },
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'html',
        content: 'test',
      })
    })
  })

  describe('edge cases', () => {
    it('handles empty string content', () => {
      const raw = JSON.stringify({
        $type: 'html',
        content: '',
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'html',
        content: '',
      })
    })

    it('handles empty string title', () => {
      const raw = JSON.stringify({
        $type: 'html',
        content: 'test',
        title: '',
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'html',
        content: 'test',
        title: '',
      })
    })

    it('ignores non-string title', () => {
      const raw = JSON.stringify({
        $type: 'html',
        content: 'test',
        title: 123,
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'html',
        content: 'test',
      })
    })

    it('handles whitespace-only content', () => {
      const raw = JSON.stringify({
        $type: 'html',
        content: '   \n\t  ',
      })
      const result = parseVisualPayload(raw)
      expect(result).toEqual({
        $type: 'html',
        content: '   \n\t  ',
      })
    })
  })
})
