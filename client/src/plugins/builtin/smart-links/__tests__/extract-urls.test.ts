import { describe, it, expect } from 'vitest'
import { extractUrls } from '../utils/extract-urls'

describe('extractUrls', () => {
  it('extracts a plain URL', () => {
    expect(extractUrls('Check this out: https://github.com/owner/repo/pull/123')).toEqual([
      'https://github.com/owner/repo/pull/123',
    ])
  })

  it('extracts multiple plain URLs', () => {
    const text = 'See https://github.com/owner/repo/pull/1 and https://linear.app/team/issue/ABC-123'
    expect(extractUrls(text)).toEqual([
      'https://github.com/owner/repo/pull/1',
      'https://linear.app/team/issue/ABC-123',
    ])
  })

  it('extracts URLs from markdown links', () => {
    expect(extractUrls('See [PR #123](https://github.com/owner/repo/pull/123) for details')).toEqual([
      'https://github.com/owner/repo/pull/123',
    ])
  })

  it('does not duplicate URLs from markdown and bare', () => {
    const text = '[PR](https://github.com/owner/repo/pull/1) and https://github.com/owner/repo/pull/1'
    expect(extractUrls(text)).toEqual(['https://github.com/owner/repo/pull/1'])
  })

  it('deduplicates repeated URLs', () => {
    const text = 'https://github.com/owner/repo/pull/1 and https://github.com/owner/repo/pull/1'
    expect(extractUrls(text)).toEqual(['https://github.com/owner/repo/pull/1'])
  })

  it('strips trailing punctuation from URLs', () => {
    expect(extractUrls('See https://github.com/owner/repo/pull/1.')).toEqual([
      'https://github.com/owner/repo/pull/1',
    ])
    expect(extractUrls('See https://github.com/owner/repo/pull/1,')).toEqual([
      'https://github.com/owner/repo/pull/1',
    ])
    expect(extractUrls('(https://github.com/owner/repo/pull/1)')).toEqual([
      'https://github.com/owner/repo/pull/1',
    ])
  })

  it('returns empty array when no URLs', () => {
    expect(extractUrls('Hello world, no links here!')).toEqual([])
  })

  it('returns empty array for empty string', () => {
    expect(extractUrls('')).toEqual([])
  })

  it('handles text with http and https URLs', () => {
    const text = 'http://example.com and https://example.org'
    expect(extractUrls(text)).toEqual(['http://example.com', 'https://example.org'])
  })

  it('extracts URLs from mixed markdown and plain text', () => {
    const text = '[Issue](https://linear.app/team/issue/ABC-1) and also https://github.com/owner/repo/pull/99'
    expect(extractUrls(text)).toEqual([
      'https://linear.app/team/issue/ABC-1',
      'https://github.com/owner/repo/pull/99',
    ])
  })

  it('extracts GitHub shorthand owner/repo#number references', () => {
    const text = 'See DuendeSoftware/Issues#1943 for details'
    expect(extractUrls(text)).toEqual([
      'https://github.com/DuendeSoftware/Issues/issues/1943',
    ])
  })

  it('extracts multiple shorthand references', () => {
    const text = 'Fixed in owner/repo#1 and owner/repo#2'
    expect(extractUrls(text)).toEqual([
      'https://github.com/owner/repo/issues/1',
      'https://github.com/owner/repo/issues/2',
    ])
  })

  it('does not duplicate when shorthand and full URL both present', () => {
    const text = 'owner/repo#123 https://github.com/owner/repo/issues/123'
    expect(extractUrls(text)).toEqual([
      'https://github.com/owner/repo/issues/123',
    ])
  })

  it('does not match bare #number without owner/repo prefix', () => {
    const text = 'See issue #42 for details'
    expect(extractUrls(text)).toEqual([])
  })

  it('strips markdown bold markers from URLs', () => {
    expect(extractUrls('See **https://github.com/owner/repo/pull/58** for details')).toEqual([
      'https://github.com/owner/repo/pull/58',
    ])
  })

  it('does not duplicate URL when both bold and plain versions exist', () => {
    const text = '**https://github.com/owner/repo/pull/58** and https://github.com/owner/repo/pull/58'
    expect(extractUrls(text)).toEqual(['https://github.com/owner/repo/pull/58'])
  })

  it('handles shorthand with dots and hyphens in names', () => {
    const text = 'Check my-org/my.repo#99'
    expect(extractUrls(text)).toEqual([
      'https://github.com/my-org/my.repo/issues/99',
    ])
  })
})
