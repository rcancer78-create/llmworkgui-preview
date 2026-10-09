// Owned file-review bridge. No local filesystem tools or shell are exposed to the model.
import { createInterface } from 'node:readline';
import { createBot, usage } from './bot.mjs';

const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
const abort = new AbortController();
let started = false, cleanupPending = false;
const bot = createBot({ warn: () => { cleanupPending = true; } });
// Validate UTF-16 before Node's UTF-8 encoder can silently replace an unpaired surrogate.
function isWellFormed(text) {
  for (let i = 0; i < text.length; i++) {
    const code = text.charCodeAt(i);
    if (code >= 0xd800 && code <= 0xdbff) {
      const next = text.charCodeAt(++i);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
    } else if (code >= 0xdc00 && code <= 0xdfff) return false;
  }
  return true;
}
process.on('SIGTERM', () => abort.abort());
process.on('SIGINT', () => abort.abort());
input.on('close', () => abort.abort());
input.on('line', async line => {
  if (started) { if (line === 'cancel') abort.abort(); return; }
  started = true;
  try {
    const request = JSON.parse(line);
    if (request.action === 'status') {
      await usage({ signal: AbortSignal.any([abort.signal, AbortSignal.timeout(30000)]) });
      process.stdout.write(JSON.stringify({ ready: true }) + '\n');
      return;
    }
    if (typeof request.prompt !== 'string' || !request.prompt.trim() || request.prompt.length > 64000
        || !isWellFormed(request.prompt) || Buffer.byteLength(request.prompt, 'utf8') > 200000)
      throw new Error('Invalid input.');
    let signal = abort.signal;
    if (request.notAfterUtc !== undefined) {
      const expiry = typeof request.notAfterUtc === 'string' ? Date.parse(request.notAfterUtc) : NaN;
      const remaining = expiry - Date.now();
      if (!Number.isFinite(expiry) || remaining <= 0 || remaining > 300000)
        throw new Error('Invalid consent deadline.');
      signal = AbortSignal.any([signal, AbortSignal.timeout(Math.ceil(remaining))]);
    }
    const answer = await bot.ask(request.prompt, { signal });
    if (!answer.text?.trim() || Buffer.byteLength(answer.text, 'utf8') > 2000000)
      throw new Error('Invalid output.');
    process.stdout.write(JSON.stringify({ text: answer.text, cleanupPending }) + '\n');
  } catch {
    // Upstream errors may contain input or session details; never forward their raw message.
    process.stdout.write(JSON.stringify({ error: abort.signal.aborted ? 'cancelled' : 'review_failed', cleanupPending }) + '\n');
    process.exitCode = 1;
  } finally {
    input.close();
    process.stdin.destroy();
  }
});
