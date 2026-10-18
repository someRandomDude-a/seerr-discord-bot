import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import test from 'node:test';

const source = name => readFile(new URL('../../src/Jellyfin.Plugin.MediaHub/' + name, import.meta.url), 'utf8');

test('watch-party boundary has no native SyncPlay implementation, discovery or invitation redemption', async () => {
  const root = new URL('../../src/Jellyfin.Plugin.MediaHub/', import.meta.url);
  for (const file of (await readdir(root)).filter(name => name.endsWith('.cs'))) {
    const text = await source(file);
    assert.doesNotMatch(text, /ISyncPlayManager|IPartyMembership|\bPartyService\b|NewGroupRequest|JoinGroupRequest|ConsumeTicket|AddInvite|CreatorSessionJoined/, file);
  }
  const integration = await source('WatchPartyIntegration.cs');
  assert.match(integration, /new\(false, false, UnavailableCode, UnavailableMessage\)/);
  assert.match(integration, /IWatchPartyService provider/);
  assert.doesNotMatch(integration, /GetKeyedService|HttpClient|GetAssemblies|GetType\(/);
  const browser = await source('Web/hub.js');
  assert.doesNotMatch(browser, /SyncPlayGroupUpdate|GroupJoined|ApiClient\.subscribe|api\('Parties/);
  const store = await source('HubStore.cs');
  for (const table of ['parties', 'invitations', 'tickets']) assert.match(store, new RegExp('CREATE TABLE IF NOT EXISTS ' + table));
});

test('Discord no longer registers watchparty or publishes/redeems party invitations; deletion consent stays separate', async () => {
  const discord = await source('DiscordHost.cs');
  assert.doesNotMatch(discord, /WithName\("watchparty"\)|Verify & join watch party|Open Jellyfin & join|store\.(Ticket|Invite|AddInvite)/);
  assert.match(discord, /CommandName == "watchparty"\) throw provider\.GetRequiredService<WatchPartyIntegration>\(\)\.Unavailable\(\)/);
  assert.match(discord, /StartsWith\("mediahub:join:", StringComparison\.Ordinal\)/);
  assert.match(discord, /const string consentPrefix = "mediahub:consent:"/);
  assert.match(discord, /DeletionQueue>\(\)\.Respond/);
});
