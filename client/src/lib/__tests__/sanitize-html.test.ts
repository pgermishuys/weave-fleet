import { describe, it, expect } from 'vitest'
import { sanitizeHtml } from '../sanitize-html'

describe('sanitizeHtml', () => {
  describe('dangerous content removal', () => {
    it('strips <script> tags', () => {
      const input = '<p>Hello</p><script>alert("xss")</script>'
      const result = sanitizeHtml(input)
      expect(result).toBe('<p>Hello</p>')
      expect(result).not.toContain('script')
      expect(result).not.toContain('alert')
    })

    it('strips <iframe> tags', () => {
      const input = '<p>Content</p><iframe src="https://evil.com"></iframe>'
      const result = sanitizeHtml(input)
      expect(result).toBe('<p>Content</p>')
      expect(result).not.toContain('iframe')
    })

    it('strips javascript: URIs from links', () => {
      const input = '<a href="javascript:alert(1)">Click me</a>'
      const result = sanitizeHtml(input)
      expect(result).not.toContain('javascript:')
      // DOMPurify removes the href entirely when it's dangerous
      expect(result).toBe('<a>Click me</a>')
    })

    it('strips javascript: URIs from images', () => {
      const input = '<img src="javascript:alert(1)" alt="test">'
      const result = sanitizeHtml(input)
      expect(result).not.toContain('javascript:')
    })

    it('rejects <input> elements', () => {
      const input = '<p>Form:</p><input type="text" value="test">'
      const result = sanitizeHtml(input)
      expect(result).toBe('<p>Form:</p>')
      expect(result).not.toContain('input')
    })

    it('strips data: URIs', () => {
      const input = '<a href="data:text/html,<script>alert(1)</script>">Click</a>'
      const result = sanitizeHtml(input)
      expect(result).not.toContain('data:')
      expect(result).toBe('<a>Click</a>')
    })

    it('strips event handlers', () => {
      const input = '<p onclick="alert(1)">Click me</p>'
      const result = sanitizeHtml(input)
      expect(result).toBe('<p>Click me</p>')
      expect(result).not.toContain('onclick')
    })
  })

  describe('allowed content', () => {
    it('allows safe HTML tags', () => {
      const input = '<h1>Title</h1><p>Paragraph with <strong>bold</strong> and <em>italic</em></p>'
      const result = sanitizeHtml(input)
      expect(result).toBe(input)
    })

    it('allows https: links', () => {
      const input = '<a href="https://example.com">Link</a>'
      const result = sanitizeHtml(input)
      expect(result).toContain('href="https://example.com"')
    })

    it('allows http: links', () => {
      const input = '<a href="http://example.com">Link</a>'
      const result = sanitizeHtml(input)
      expect(result).toContain('href="http://example.com"')
    })

    it('allows mailto: links', () => {
      const input = '<a href="mailto:test@example.com">Email</a>'
      const result = sanitizeHtml(input)
      expect(result).toContain('href="mailto:test@example.com"')
    })

    it('allows images with https: src', () => {
      const input = '<img src="https://example.com/image.png" alt="Test">'
      const result = sanitizeHtml(input)
      expect(result).toContain('src="https://example.com/image.png"')
      expect(result).toContain('alt="Test"')
    })

    it('allows lists', () => {
      const input = '<ul><li>Item 1</li><li>Item 2</li></ul>'
      const result = sanitizeHtml(input)
      expect(result).toBe(input)
    })

    it('allows tables', () => {
      const input = '<table><thead><tr><th>Header</th></tr></thead><tbody><tr><td>Cell</td></tr></tbody></table>'
      const result = sanitizeHtml(input)
      expect(result).toBe(input)
    })

    it('allows SVG elements for Mermaid', () => {
      const input = '<svg viewBox="0 0 100 100"><circle cx="50" cy="50" r="40" fill="blue" /></svg>'
      const result = sanitizeHtml(input)
      expect(result).toContain('<svg')
      expect(result).toContain('viewBox')
      expect(result).toContain('<circle')
      expect(result).toContain('cx')
      expect(result).toContain('fill')
    })

    it('allows code blocks', () => {
      const input = '<pre><code>const x = 42;</code></pre>'
      const result = sanitizeHtml(input)
      expect(result).toBe(input)
    })

    it('allows blockquotes', () => {
      const input = '<blockquote>Quote text</blockquote>'
      const result = sanitizeHtml(input)
      expect(result).toBe(input)
    })
  })

  describe('rel attribute enforcement', () => {
    it('forces rel="noopener noreferrer" on links with target attribute', () => {
      const input = '<a href="https://example.com" target="_blank">Link</a>'
      const result = sanitizeHtml(input)
      expect(result).toContain('rel="noopener noreferrer"')
      expect(result).toContain('target="_blank"')
    })

    it('overrides existing rel attribute on links with target', () => {
      const input = '<a href="https://example.com" target="_blank" rel="nofollow">Link</a>'
      const result = sanitizeHtml(input)
      expect(result).toContain('rel="noopener noreferrer"')
      expect(result).not.toContain('rel="nofollow"')
    })

    it('does not add rel to links without target attribute', () => {
      const input = '<a href="https://example.com">Link</a>'
      const result = sanitizeHtml(input)
      expect(result).not.toContain('rel=')
    })

    it('handles multiple links with different target attributes', () => {
      const input = '<a href="https://example.com" target="_blank">External</a><a href="/internal">Internal</a>'
      const result = sanitizeHtml(input)
      expect(result).toContain('rel="noopener noreferrer"')
      // Count occurrences - should only be one rel attribute
      const relCount = (result.match(/rel=/g) || []).length
      expect(relCount).toBe(1)
    })
  })

  describe('edge cases', () => {
    it('handles empty string', () => {
      const result = sanitizeHtml('')
      expect(result).toBe('')
    })

    it('handles plain text', () => {
      const input = 'Just plain text'
      const result = sanitizeHtml(input)
      expect(result).toBe(input)
    })

    it('handles malformed HTML', () => {
      const input = '<p>Unclosed paragraph<div>Nested</div>'
      const result = sanitizeHtml(input)
      // DOMPurify will fix the structure
      expect(result).not.toContain('<script>')
      expect(result).toContain('Unclosed paragraph')
      expect(result).toContain('Nested')
    })

    it('handles mixed safe and unsafe content', () => {
      const input = '<p>Safe</p><script>alert(1)</script><strong>Bold</strong>'
      const result = sanitizeHtml(input)
      expect(result).toBe('<p>Safe</p><strong>Bold</strong>')
    })
  })
})
