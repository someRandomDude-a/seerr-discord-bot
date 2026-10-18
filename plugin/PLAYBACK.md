# Real native player destinations

Media Hub does not create players, log in as another user, fabricate playback reports or forward
native credentials to Discord/Activity. This feature uses Jellyfin's supported `SendPlayCommand`
with an actual authenticated source session and an actual capable **same-account** destination.

## Use

1. Open a real Jellyfin player and sign in as your own account. Its active controller and declared
   playback/media-control capabilities must be present in Jellyfin's current session registry.
2. On a Media Hub native detail view, choose **one of your real Jellyfin players**; the Discord
   private detail view offers **Choose your player**. Other users' sessions are never listed or
   accepted, including for administrators. Individually playable movies/episodes/audio only: use
   Jellyfin to choose episodes/tracks from a series/album/folder first.
3. Select a destination and review the two-minute preview. **Confirm native play command** actually
   sends `PlayNow` and may interrupt the destination's current playback. Preparing/cancelling a
   preview and refreshing a receipt do not send.
4. `accepted` means the native command call returned—not that playback began. A reported current
   item is an observation, not proof that this command caused it. Native players remain responsible
   for progress/completion/count updates; Media Hub never fabricates those events.

Activity cannot call these player routes. Its explicit native-control handoff opens Jellyfin
without a native credential or playback/join claim. When a registered Private SyncPlay provider
reports source or destination party membership, native destination commands are rejected: use the
companion's group playback/queue controls instead.

## Persistence and uncertain results

Schema **12** adds only the plugin-owned `playback_operations` journal after the usual verified
pre-upgrade snapshot; old party/consent/history/configuration records stay intact. The encrypted
preview binds its ID, native user, source session, destination session/device, item, configuration,
expiry and state. A conditional durable `sending` claim precedes the native call. Changed state
alone cannot turn a consumed command into another send. Invalid records remain in place and fail closed.

There is one bounded command slot and a ten-second cancellation budget. An uncooperative native
call retains the slot until it completes. Sending/uncertain receipts are not automatically replayed
after timeout, refresh or restart; a duplicate same-item/target preview is blocked.

Use **Your native player commands → Review receipts** to find retained outcomes after navigation
or restart. Inspect the actual player first. **Acknowledge manual inspection** records `reviewed`
without sending, rolling back, claiming failure or proving cancellation. A deliberately prepared
later command can still duplicate an earlier effect; there is no exactly-once player guarantee.

Receipts are not deleted automatically. A 5,000-record global journal, 500 retained receipts and 20 live pending previews per
user and bounded session/history scans fail closed at capacity. Full valid-snapshot replay and
host/key compromise remain documented trust boundaries, not solved by this journal.

Actual playback, disconnect/permission/session changes, simultaneous clients and companion/native
concurrency still require isolated real-device/media acceptance. Unit/native-host smoke tests do
not authorize production activation.
