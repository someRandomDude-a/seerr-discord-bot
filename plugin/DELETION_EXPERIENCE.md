# Deletion experience · active development

## Implemented in this increment

- Native queue rows offer a passive **Status and your required action** view:
  deadline, minimum delay, automatic-removal state, the caller's own last recorded
  activity/consent and exact user-bound Yes/No controls. Other viewers' identities,
  activity timestamps, requester/request IDs and target paths are not returned.
- Unknown activity is explicitly unknown, not an empty verified scan. New activity
  invalidates old consent; only a fresh queue recheck issues a new bound notice.
- A focused **Your notification preferences** page is linked from the notification
  bell and queue. Personal delay, notification choices and a customizable deletion
  notice message use the existing revision/conflict checks and protected state.
- Optional messages support only `{title}`, `{delayDays}`, `{activityDays}` and
  300 characters. They are presentation, never permission or deletion instructions.
  Native clients show the message as text behind Help while retaining the compact
  **Allow deletion? / Yes / No / Help** controls. Discord uses the same personalized
  notice without mentions; markup is escaped. Blank restores the default.
- Null message properties are omitted from serialization: retained default-valued
  preferences and signed server settings are not silently rewritten by the new field.
- Advanced manual ID entry is collapsed; users can propose from existing native
  item controls. Empty queues explain how to start from a title.
- Administrator policy defaults to own requests, with denied/own-request/any-item
  defaults and bounded native-account overrides. The native editor requires an
  explicit revision-bound save and blocks resubmission after an uncertain write.
- Explicit any-item grants support verified native movies and whole series only.
  All approved configured Arr destinations must be scanned successfully and yield
  one provider/path/file target. Visibility, current permission, consent and the full
  delay remain mandatory; zero request IDs require an explicit native source proof.
- Native detail notification settings and a Jellyfin context-menu extension are
  implemented without replacing native commands or persisted Web files. Unsupported
  menu structures are left unchanged. Opening settings is passive; subscribing is
  an explicit, revision-bound mutation. Real Jellyfin menu/keyboard acceptance is pending.
- The separate Discord command flow reads core permissions, uses the same native
  proposal operation under an any-item grant, and offers caller-private status.
- [Authenticated administrator history](ADMIN_MONITORING.md) records new transitions
  while explicitly preserving gaps for proposals created by older builds.

`GET /MediaHub/Deletions/{id}/Status` and the in-process `deletions.status` operation
are user-scoped, passive snapshots, not live Arr verification or execution approval.
They do not record activity, vote, mark notices seen or change queue deadlines.
The existing separate [administrator evidence workspace](ADMIN_MONITORING.md)
remains the only identity-rich projection.

## Still required

- Exact-ZIP service/permission-race acceptance of the new authority, including
  whole-series scope and missing-native-item cleanup (currently fail-closed).
- Actual Jellyfin item-menu, richer item-specific status and refresh/focus acceptance.
- Broader audit failure/backlog presentation; older records never acquire a fabricated timeline.
- Broader notification lifecycle/status customization and real service/browser
  acceptance of this increment, including root/non-root installations and themes.

This source increment is newer than the `split-20261018-r2` ZIPs. No old artifact
has been overwritten or represented as acceptance of these changes. Feature parity
and automatic production activation remain disabled; the Private SyncPlay SDK
dependency stays in the separate Discord plugin.
