import { build } from 'esbuild';
import { fileURLToPath } from 'node:url';
import { readFile, readdir } from 'node:fs/promises';

const packages = ['@discord/embedded-app-sdk', 'big-integer', 'decimal.js-light', 'eventemitter3', 'lodash.transform', 'uuid', 'zod'];
const notices = await Promise.all(packages.map(async name => {
  const directory = new URL('./node_modules/' + name + '/', import.meta.url);
  const filename = (await readdir(directory)).find(file => /^licen[sc]e(?:\.|$)/i.test(file));
  if (!filename) throw new Error('Missing bundled dependency license: ' + name);
  return name + '\n' + await readFile(new URL(filename, directory), 'utf8');
}));
await build({ entryPoints: [fileURLToPath(new URL('./src/main.js', import.meta.url))], bundle: true, format: 'esm', target: 'es2022',
  banner: { js: '/*! Bundled dependency notices\n' + notices.join('\n\n').replaceAll('*/', '* /') + '\n*/' },
  minify: true, legalComments: 'inline', outfile: fileURLToPath(new URL('../src/Jellyfin.Plugin.MediaHub/Web/activity.bundle.js', import.meta.url)) });
