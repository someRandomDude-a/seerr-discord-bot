import test from 'node:test';
import assert from 'node:assert/strict';
import { formatBytes, reference, identity } from '../src/dom.js';

test('storage units are bounded and consistent', () => {
  assert.equal(formatBytes(1024), '1.0 KiB');
  assert.equal(formatBytes(-10), '0.0 B');
  assert.equal(formatBytes(1024 ** 4), '1.0 TiB');
});

test('item references exclude upstream payloads and secrets', () => {
  const ref = reference({ kind: 'movie', external_id: '10', title: 'A film', raw: { secret: 'not-public' } });
  assert.equal(ref.raw, undefined);
  assert.equal(ref.external_id, '10');
  assert.equal(identity({ ...ref, id: 3 }), 'movie:10:3:');
});
