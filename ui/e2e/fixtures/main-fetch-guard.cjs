const { appendFileSync, existsSync, readFileSync } = require('node:fs');

const file = process.env.SCOUT_E2E_FETCH_LOG;
const ollamaFile = process.env.SCOUT_E2E_OLLAMA;
const OLLAMA = 'http://localhost:11434';

function json(body, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

const pulled = [];

function pullStream(name, missing) {
  const lines = missing
    ? [{ status: 'pulling manifest' }, { error: 'pull model manifest: file does not exist' }]
    : [
        { status: 'pulling manifest' },
        { status: 'pulling 0a1b2c3d', total: 100, completed: 50 },
        { status: 'pulling 0a1b2c3d', total: 100, completed: 100 },
        { status: 'success' },
      ];
  if (!missing && !pulled.includes(name)) pulled.push(name);
  return new Response(lines.map((line) => `${JSON.stringify(line)}\n`).join(''), {
    status: 200,
    headers: { 'content-type': 'application/x-ndjson' },
  });
}

function answerAsOllama(url, init) {
  if (!ollamaFile || !existsSync(ollamaFile)) return null;
  const ollama = JSON.parse(readFileSync(ollamaFile, 'utf8'));
  if (url === `${OLLAMA}/api/tags`) {
    const listed = ollama.tags.map((tag) => tag.name);
    const added = pulled
      .filter((name) => !listed.includes(name))
      .map((name) => ({ name, model: name, modified_at: '2026-11-12T00:00:00Z', size: 0, details: {} }));
    return json({ models: [...ollama.tags, ...added] });
  }
  if (url === `${OLLAMA}/api/pull`) {
    const asked = JSON.parse(String(init && init.body)).name;
    const name = asked.includes(':') ? asked : `${asked}:latest`;
    return pullStream(name, (ollama.missing || []).includes(name));
  }
  if (url === `${OLLAMA}/api/show`) {
    const model = JSON.parse(String(init && init.body)).model;
    const capabilities = ollama.capabilities[model];
    return capabilities === undefined
      ? json({ error: `model '${model}' not found` }, 404)
      : json({ capabilities });
  }
  return null;
}

if (file) {
  globalThis.fetch = (input, init) => {
    const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
    appendFileSync(file, `${url}\n`);
    const answer = answerAsOllama(url, init);
    if (answer !== null) return Promise.resolve(answer);
    return Promise.reject(new TypeError(`fetch blocked by the end-to-end tests: ${url}`));
  };
}
