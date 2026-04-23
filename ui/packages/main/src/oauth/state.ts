import { randomBytes, createHash } from 'node:crypto';

function toBase64Url(buf: Buffer): string {
  return buf.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=/g, '');
}

export function generateState(): string {
  return toBase64Url(randomBytes(32));
}

export function generatePkcePair(): { verifier: string; challenge: string } {
  const verifier = toBase64Url(randomBytes(43)).slice(0, 128);
  const challenge = toBase64Url(createHash('sha256').update(verifier).digest());
  return { verifier, challenge };
}
