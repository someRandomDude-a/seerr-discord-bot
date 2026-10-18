# Deletion administration · development preview

Open the native plugin configuration page as a **Jellyfin administrator**. The
**Deletion administration** workspace is separate from ordinary queue and bell
responses. It is not available through Discord Activity or the public user RPC.

## Browsing and monitoring

- Active backlog, completed/cancelled history or an individual state.
- Requester account filter, proposal/Seerr request ID search, oldest/newest/due
  order and bounded 25-row pages.
- Global state totals, current automatic-removal setting and snapshot time.
- Explicit evidence inspection; opt-in refresh while the page is visible.
- Owner identities (including disabled/unavailable accounts), queue revisions,
  consent/deadline integrity and retained execution checkpoints.
- Current activity with account names, recorded votes and the exact activity
  timestamps they cover, consent-notice/Discord delivery backlog.
- Authenticated retained Arr destination/edition/path/file proof where recorded.
  No API key, native token or proof ciphertext is presented.

All reads are passive. They do not vote, mark updates seen, create activity,
reverify upstream services, retry uncertain writes, change state or remove media.
The displayed proof's integrity is **retained cryptographic integrity**, not a
claim of fresh Seerr/Arr verification. An unavailable activity scan is labelled
unknown, never shown as a verified empty viewer list. Invalid consent/deadline
integrity is shown as unverified and is not resealed by monitoring.

## History limitations

Completed/cancelled proposals remain browsable even when the media has disappeared.
Unavailable accounts are labelled honestly. New proposals retain an encrypted title
snapshot for administrator history after removal. Older proposals without a recorded
title remain labelled unavailable; no title or earlier transition is invented.

Schema **14** adds an encrypted transition journal and private authenticated heads;
format-3 settings, existing keys, schema-13 roots/seals and proposal state are retained.
`GET /MediaHub/Admin/Deletions/{id}/History?page=1` returns bounded 25-event pages.
Creation, consent, changed scheduling/blocking state, cancellation and execution
checkpoints are recorded. Unchanged polling is not a new event. Failure reasons are
bounded categories, never raw upstream errors or credentials. This is a transition
journal, not a complete record of every unchanged verification attempt or delivery.

History states whether recording began at creation or later. Valid older consent
seals remain usable; the first later event does not retroactively claim completeness.
Altered, omitted or replayed events fail verification. A torn publication/commit
pair blocks the affected proposal; no old root is automatically promoted. Restore
only a consistent stopped whole-directory backup. Whole-snapshot rollback and an
OS/key compromise remain explicit trust boundaries. Full split runtime acceptance
and broader backlog/failure presentation remain pending.

## Authorization and privacy

`GET /MediaHub/Admin/Deletions`, evidence and history endpoints require
native user authentication, Jellyfin elevation policy and live administrator
status. An API key or a caller-supplied administrator ID is not accepted. Account
disable or loss of administration blocks access. Responses are no-store; private
DOM clears on page/account/settings teardown or failed monitoring verification.

Ordinary `/MediaHub/Deletions`, `/MediaHub/Me/Consent` and notification responses
are unchanged: no other users' identities, votes, paths, checkpoints or activity
are added to them. An administrator inspecting their personal queue uses the
separate elevated evidence endpoint, not a broadened ordinary DTO.
