// Writes the Google OAuth Desktop client into the packaged app when SCOUT_GOOGLE_CLIENT_SECRET is set.
// The secret comes from the build environment only (a CI secret or the owner's shell); never commit the file.
// Usage: node build/scripts/write-google-client.mjs [target]
import { readFileSync } from 'node:fs';
import { rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';

const uiRoot = join(import.meta.dirname, '..', '..');
const target =
  process.argv[2] ?? join(uiRoot, 'packages', 'main', 'resources', 'google-client.json');
const secret = process.env.SCOUT_GOOGLE_CLIENT_SECRET ?? '';
const source = readFileSync(
  join(uiRoot, 'packages', 'main', 'src', 'oauth', 'google-auth.ts'),
  'utf8',
);
const clientId =
  process.env.SCOUT_GOOGLE_CLIENT_ID ||
  (/DEFAULT_GOOGLE_CLIENT_ID\s*=\s*'([^']*)'/.exec(source)?.[1] ?? '');

if (secret.length === 0 || clientId.length === 0) {
  await rm(target, { force: true });
  console.log('google client: not bundled');
} else {
  await writeFile(target, JSON.stringify({ client_id: clientId, client_secret: secret }));
  console.log('google client: bundled');
}
