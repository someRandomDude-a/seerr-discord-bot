import assert from 'node:assert/strict';
import { readdir, readFile, stat } from 'node:fs/promises';
import { extname, join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const root = fileURLToPath(new URL('../../../', import.meta.url));
const excluded = new Set(['.git', 'node_modules', 'bin', 'obj', 'artifacts', 'TestResults']);
async function sources(directory = root) {
  const result = [];
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (excluded.has(entry.name) || entry.name === 'activity.bundle.js' || entry.isSymbolicLink()) continue;
    const path = join(directory, entry.name);
    if (entry.isDirectory()) result.push(...await sources(path));
    else result.push(path);
  }
  return result;
}

test('repository product sources are confined to the plugin and its documentation', async () => {
  const directories = (await readdir(root, { withFileTypes: true })).filter(entry => entry.isDirectory() && !entry.name.startsWith('.')).map(entry => entry.name).sort();
  assert.deepEqual(directories, ['docs', 'plugin']);
  for (const path of await sources()) assert.ok(!['.py', '.pyc', '.pyo'].includes(extname(path).toLowerCase()), `Unexpected standalone runtime source: ${path}`);
  const readme = await readFile(join(root, 'README.md'), 'utf8');
  assert.match(readme, /^# Media Hub for Jellyfin 12/);
  assert.match(readme, /integrated tutorial/);
  assert.match(readme, /plugin\/FEATURES\.md/);
});

test('all active workflows target plugin testing or catalog publication', async () => {
  const directory = join(root, '.github', 'workflows');
  const entries = (await readdir(directory)).filter(name => /\.ya?ml$/.test(name));
  assert.ok(entries.length >= 2);
  for (const name of entries) {
    const content = await readFile(join(directory, name), 'utf8');
    assert.match(content, /plugin\//);
    assert.doesNotMatch(content, /pip install|python -m|docker\/build-push-action|working-directory:\s*activity\s*$/m);
  }
});

test('plugin documentation has no broken local file links', async () => {
  let checked = 0;
  for (const path of (await sources()).filter(path => extname(path) === '.md')) {
    const content = await readFile(path, 'utf8');
    for (const match of content.matchAll(/\[[^\]\n]+\]\(([^\s)]+)\)/g)) {
      const target = match[1];
      if (/^(?:https?:\/\/|mailto:|#)/.test(target)) continue;
      const file = target.split('#')[0];
      if (file) assert.ok((await stat(resolve(dirname(path), decodeURIComponent(file)))).isFile(), `Broken documentation link: ${path} -> ${target}`);
      checked++;
    }
  }
  assert.ok(checked > 20, 'Expected setup, feature, safety and release documentation links.');
});
