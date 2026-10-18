import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import test from 'node:test';

const source = name => readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/' + name, import.meta.url), 'utf8');
const adapter = name => readFile(new URL('../../src/Jellyfin.Plugin.MediaHub.Discord/' + name, import.meta.url), 'utf8');

test('watch-party boundary has no native SyncPlay implementation, discovery or invitation redemption', async () => {
  const root = new URL('../../src/Jellyfin.Plugin.MediaHub/', import.meta.url);
  for (const file of (await readdir(root)).filter(name => name.endsWith('.cs'))) {
    const text = await source(file);
    assert.doesNotMatch(text, /ISyncPlayManager|IPartyMembership|\bPartyService\b|NewGroupRequest|JoinGroupRequest|ConsumeTicket|AddInvite|CreatorSessionJoined/, file);
  }
  const integration = await source('WatchPartyIntegration.cs');
  assert.match(integration, /return new\(ready, false,/);
  assert.doesNotMatch(integration, /PrivateSyncPlay\.Api|IWatchPartyService|HttpClient|GetAssemblies|GetType\(/);
  const implementation = await adapter('WatchPartyIntegration.cs');
  assert.match(implementation, /IWatchPartyService provider/);
  assert.doesNotMatch(implementation, /GetKeyedService|HttpClient|GetAssemblies|GetType\(/);
  const browser = await source('Web/hub.js');
  assert.doesNotMatch(browser, /SyncPlayGroupUpdate|GroupJoined|ApiClient\.subscribe|api\('Parties/);
  const store = await source('HubStore.cs');
  for (const table of ['parties', 'invitations', 'tickets']) assert.match(store, new RegExp('CREATE TABLE IF NOT EXISTS ' + table));
});

test('Discord watchparty uses provider-generated links, never legacy admission or invitation redemption', async () => {
  const discord = await adapter('DiscordHost.cs');
  assert.doesNotMatch(discord, /Verify & join watch party|Open Jellyfin & join|store\.(Ticket|Invite|AddInvite)/);
  const flow = await adapter('DiscordWatchParty.cs');
  assert.match(flow, /commands\.Invite\(/); assert.match(flow, /url: link\.Url/);
  assert.doesNotMatch(flow, /JoinGroup|Redeem|ConsumeTicket|GetSessionByAuthenticationToken/);
  assert.match(discord, /StartsWith\("mediahub:join:", StringComparison\.Ordinal\)/);
  assert.match(discord, /const string consentPrefix = "mediahub:consent:"/);
  assert.match(discord, /DeletionQueue>\(\)\.Respond/);
});
