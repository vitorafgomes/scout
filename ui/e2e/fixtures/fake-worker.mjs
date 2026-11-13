import { appendFileSync, existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { createInterface } from 'node:readline';

const here = path.dirname(new URL(import.meta.url).pathname);
const script = JSON.parse(readFileSync(path.join(here, 'script.json'), 'utf8'));
const cursor = '▌';
const cancelled = new Map();
let asked = 0;
let meetings = [...(script.meetings ?? [])];

function send(event) {
  process.stdout.write(`SCOUT_LOCAL ${JSON.stringify(event)}\n`);
}

function pause(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function released(file) {
  while (!existsSync(path.join(here, file))) await pause(25);
}

async function answer(id, reply) {
  let stop;
  const cancel = new Promise((resolve) => (stop = resolve));
  cancelled.set(id, stop);
  send({ id, event: 'begin' });
  for (const event of reply.events ?? []) send({ id, ...event });
  let text = '';
  for (const part of reply.updates ?? []) {
    await pause(reply.delayMs ?? 20);
    text += part;
    send({ id, event: 'update', text: text + cursor });
  }
  const finished = reply.holdUntilCancel
    ? new Promise(() => {})
    : reply.holdUntilFile != null
      ? released(reply.holdUntilFile)
      : pause(0);
  const outcome = await Promise.race([
    cancel.then(() => 'cancel'),
    finished.then(() => 'complete'),
  ]);
  cancelled.delete(id);
  for (const event of reply.endEvents ?? []) send({ id, ...event });
  if (outcome === 'cancel') send({ id, event: 'fail', text: 'Cancelled.' });
  else send({ id, event: 'complete', text: reply.complete ?? text, citations: [] });
}

process.stdout.write('SCOUT_READY\n');

createInterface({ input: process.stdin }).on('line', (line) => {
  appendFileSync(path.join(here, 'received.jsonl'), `${line}\n`);
  const request = JSON.parse(line);
  if (request.type === 'cancel') cancelled.get(request.id)?.();
  if (request.type === 'meetings') {
    send({ id: request.id, event: 'meetings', items: meetings });
  }
  if (request.type === 'delete') {
    const kept = request.all === true ? [] : meetings.filter((m) => m.id !== request.meetingId);
    send({ id: request.id, event: 'deleted', count: meetings.length - kept.length });
    meetings = kept;
  }
  if (request.type !== 'ask') return;
  const replies = script.replies ?? [];
  const reply = replies[Math.min(asked, replies.length - 1)] ?? { complete: 'OK' };
  asked += 1;
  void answer(request.id, reply);
});

process.stdin.on('end', () => process.exit(0));
