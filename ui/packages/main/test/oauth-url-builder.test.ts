import { describe, it, expect } from 'bun:test';
import { buildAuthorizeUrl } from '../src/oauth/url-builder.js';

const BASE_PARAMS = {
  clientId: 'client123',
  permissions: '8',
  redirectUri: 'http://127.0.0.1:53682/callback',
  state: 'abc-state',
  codeChallenge: 'xyz-challenge',
};

describe('buildAuthorizeUrl', () => {
  it('targets the Discord oauth2 authorize endpoint', () => {
    const url = buildAuthorizeUrl(BASE_PARAMS);
    expect(url.startsWith('https://discord.com/oauth2/authorize?')).toBe(true);
  });

  it('includes client_id', () => {
    const parsed = new URL(buildAuthorizeUrl(BASE_PARAMS));
    expect(parsed.searchParams.get('client_id')).toBe('client123');
  });

  it('scope contains bot and applications.commands', () => {
    const parsed = new URL(buildAuthorizeUrl(BASE_PARAMS));
    const scope = parsed.searchParams.get('scope') ?? '';
    expect(scope.split(' ')).toContain('bot');
    expect(scope.split(' ')).toContain('applications.commands');
  });

  it('includes permissions', () => {
    const parsed = new URL(buildAuthorizeUrl(BASE_PARAMS));
    expect(parsed.searchParams.get('permissions')).toBe('8');
  });

  it('redirect_uri is URL-encoded and round-trips correctly', () => {
    const parsed = new URL(buildAuthorizeUrl(BASE_PARAMS));
    expect(parsed.searchParams.get('redirect_uri')).toBe('http://127.0.0.1:53682/callback');
  });

  it('response_type is code', () => {
    const parsed = new URL(buildAuthorizeUrl(BASE_PARAMS));
    expect(parsed.searchParams.get('response_type')).toBe('code');
  });

  it('includes state', () => {
    const parsed = new URL(buildAuthorizeUrl(BASE_PARAMS));
    expect(parsed.searchParams.get('state')).toBe('abc-state');
  });

  it('includes code_challenge', () => {
    const parsed = new URL(buildAuthorizeUrl(BASE_PARAMS));
    expect(parsed.searchParams.get('code_challenge')).toBe('xyz-challenge');
  });

  it('code_challenge_method is S256', () => {
    const parsed = new URL(buildAuthorizeUrl(BASE_PARAMS));
    expect(parsed.searchParams.get('code_challenge_method')).toBe('S256');
  });

  it('redirect_uri with special chars encodes correctly', () => {
    const params = { ...BASE_PARAMS, redirectUri: 'http://127.0.0.1:53683/callback' };
    const parsed = new URL(buildAuthorizeUrl(params));
    expect(parsed.searchParams.get('redirect_uri')).toBe('http://127.0.0.1:53683/callback');
  });
});
