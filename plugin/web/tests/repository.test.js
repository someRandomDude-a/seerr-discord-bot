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
  assert.match(readme, /^# Watchlist Deletion and Discord Integration for Jellyfin 12/);
  assert.match(readme, /two separate in-process/);
  assert.match(readme, /Discord settings belong to/);
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

test('build, catalog and CI target only the reviewed latest stable Jellyfin', async () => {
  const props = await readFile(join(root, 'plugin/Directory.Build.props'), 'utf8');
  const metadata = JSON.parse(await readFile(join(root, 'plugin/repository/plugin.json'), 'utf8'));
  const version = props.match(/<SupportedJellyfinVersion>([^<]+)<\/SupportedJellyfinVersion>/)?.[1];
  assert.equal(version, '12.2.0'); assert.equal(metadata.targetAbi, version);
  assert.match(props, /RequireSupportedJellyfin/);
  for (const name of ['plugin.yml', 'plugin-release.yml']) {
    const workflow = await readFile(join(root, '.github/workflows', name), 'utf8');
    assert.doesNotMatch(workflow, /12\.0\.0|matrix\.jellyfin/);
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
test('release publication is gated on bundled release-version tests and the exact installed ZIP/native browser', async () => {
  for (const name of ['plugin.yml', 'plugin-release.yml']) {
    const workflow = await readFile(join(root, '.github/workflows', name), 'utf8');
    assert.ok(workflow.indexOf('npm run build') < workflow.indexOf('dotnet test'));
    assert.match(workflow, /dotnet test[^\n]+RequireActivityBundle=true/);
  }
  const release = await readFile(join(root, '.github/workflows/plugin-release.yml'), 'utf8');
  assert.match(release, /dotnet test[^\n]+AssemblyVersion=/); assert.ok(release.indexOf('Test-ReleaseSmoke.ps1') < release.indexOf('actions/upload-artifact'));
  const smoke = await readFile(join(root, 'plugin/tools/Test-ReleaseSmoke.ps1'), 'utf8');
  for (const required of ['-Mode Install -Package $Package', '-Mode Restart', 'Test-NativeUi.mjs', '-Mode Uninstall', 'Get-FileHash', 'finally', 'org.mediahub.acceptance']) assert.ok(smoke.includes(required));
  assert.match(smoke, /Test-Completion\.ps1/); assert.match(smoke, /\$AxeFile/);
  assert.match(smoke, /Test-LibraryAcceptance\.ps1/);
  assert.match(smoke, /Test-ProviderAcceptance\.ps1/);
  assert.match(release, /d467ba8f4122dcb015f80afac5c18bad02037200fcf727c3bff88d8ea0d2ea8a/);
  assert.match(release, /axe-core@4\.14\.0/);
});
test('native web smoke uses pinned upstream dependencies but runtime checks follow upstream minimum/API guidance', async () => {
  const lock = JSON.parse(await readFile(join(root, 'plugin/repository/web-dependencies.json'), 'utf8'));
  assert.equal(lock.targetAbi, '12.2.0'); assert.equal(lock.packages.length, 2);
  for (const dependency of lock.packages) { assert.equal(dependency.version, '3.0.2.0'); assert.match(dependency.sha256, /^[a-f0-9]{64}$/); assert.match(dependency.sourceUrl, /Release-12\.2\.0\.zip$/); }
  const runtime = await readFile(join(root, 'plugin/src/Jellyfin.Plugin.MediaHub/WebIntegration.cs'), 'utf8');
  assert.match(runtime, /AssemblyLoadContext\.All/); assert.match(runtime, /version < Version\.Parse/); assert.doesNotMatch(runtime, /version != Version\.Parse/);
  const injection = await readFile(join(root, 'plugin/src/Jellyfin.Plugin.MediaHub/ItemControlsRegistration.cs'), 'utf8');
  assert.match(injection, /fileNamePattern = "index\.html"/);
  const smoke = await readFile(join(root, 'plugin/tools/Test-ReleaseSmoke.ps1'), 'utf8');
  assert.ok(smoke.indexOf('Test-WebDependencies.ps1') < smoke.indexOf('Test-NativeUi.mjs'));
  const native = await readFile(join(root, 'plugin/tools/Test-NativeUi.mjs'), 'utf8');
  assert.doesNotMatch(native, /addScriptTag/); assert.match(native, /OrdinaryViewerPages: true/); assert.match(native, /assert\.equal\(viewerWrites, 0\)/);
});

test('one shared catalog publisher builds and verifies both separate plugins before Pages deployment', async () => {
  const release = await readFile(join(root, '.github/workflows/plugin-release.yml'), 'utf8');
  for (const project of ['MediaHub.Tests', 'MediaHub.Discord.Tests']) assert.ok(release.includes(`plugin/tests/${project}/`));
  assert.match(release, /--plugin both/);
  assert.match(release, /--discord-metadata plugin\/repository\/discord\.json/);
  assert.match(release, /for prefix in plugin discord/);
  assert.match(release, /Test-CataloguePublication\.ps1/);
  assert.match(release, /Test-Split\.ps1[^\n]+-DiscordPackage/);
  assert.match(release, /Publish-PluginCatalogue\.ps1[^\n]+-Publish/);
  assert.match(release, /repository:\s+needs: release/);
  const publisher = await readFile(join(root, 'plugin/tools/Publish-PluginCatalogue.ps1'), 'utf8');
  assert.match(publisher, /'plugin-v'/); assert.match(publisher, /'discord-v'/);
  assert.match(publisher, /SHA256/); assert.match(publisher, /--draft=false/);
  assert.ok(publisher.indexOf('foreach ($asset in $files) { if (!(Same-File') < publisher.indexOf('--draft=false'));
  assert.doesNotMatch(publisher, /['"]--clobber['"]/);
  for (const path of ['README.md', 'plugin/README.md', 'plugin/discord/README.md']) {
    const readme = await readFile(join(root, path), 'utf8');
    assert.match(readme, /Publish Jellyfin plugin repository/);
    assert.doesNotMatch(readme, /core publisher does not currently publish|core release workflow publishes the core/);
  }
});
