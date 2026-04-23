import { describe, it, expect } from 'bun:test';
import { createHash } from 'node:crypto';
import { generateState, generatePkcePair } from '../src/oauth/state.js';

function fromBase64Url(s: string): Buffer {
  const padded = s.replace(/-/g, '+').replace(/_/g, '/');
  const pad = (4 - (padded.length % 4)) % 4;
  return Buffer.from(padded + '='.repeat(pad), 'base64');
}

describe('generateState', () => {
  it('returns a base64url string with no padding', () => {
    const state = generateState();
    expect(typeof state).toBe('string');
    expect(state).not.toContain('=');
    expect(state).not.toContain('+');
    expect(state).not.toContain('/');
  });

  it('encodes 32 bytes so decoded length is 32', () => {
    const state = generateState();
    const decoded = fromBase64Url(state);
    expect(decoded.length).toBe(32);
  });

  it('produces different values on successive calls', () => {
    const a = generateState();
    const b = generateState();
    expect(a).not.toBe(b);
  });
});

describe('generatePkcePair', () => {
  it('returns verifier and challenge as strings', () => {
    const { verifier, challenge } = generatePkcePair();
    expect(typeof verifier).toBe('string');
    expect(typeof challenge).toBe('string');
  });

  it('verifier length is at least 43 chars (RFC 7636 minimum)', () => {
    const { verifier } = generatePkcePair();
    expect(verifier.length).toBeGreaterThanOrEqual(43);
    expect(verifier.length).toBeLessThanOrEqual(128);
  });

  it('challenge is 43 chars (base64url of sha256 = 32 bytes unpadded)', () => {
    const { challenge } = generatePkcePair();
    expect(challenge.length).toBe(43);
  });

  it('challenge equals sha256(verifier) as base64url unpadded', () => {
    const { verifier, challenge } = generatePkcePair();
    const expected = createHash('sha256')
      .update(verifier)
      .digest('base64')
      .replace(/\+/g, '-')
      .replace(/\//g, '_')
      .replace(/=/g, '');
    expect(challenge).toBe(expected);
  });

  it('verifier contains only base64url characters', () => {
    const { verifier } = generatePkcePair();
    expect(/^[A-Za-z0-9\-_]+$/.test(verifier)).toBe(true);
  });

  it('two calls produce different verifiers', () => {
    const a = generatePkcePair();
    const b = generatePkcePair();
    expect(a.verifier).not.toBe(b.verifier);
  });
});
