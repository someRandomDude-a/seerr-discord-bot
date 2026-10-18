# Jellyfin skin compatibility

Media Hub's **native Jellyfin pages** use Jellyfin Web's component registration and stylesheet
classes, not a plugin theme. The template imports `emby-input`, `emby-select`, `emby-checkbox`,
`emby-button` and `emby-textarea`. There is no plugin stylesheet, inline visual styling, fixed
palette, font override or copied Jellyfin CSS on the native page.

- Text fields/selects/textareas provide the component's `label` attribute and the native
  `inputContainer`/`selectContainer`. Jellyfin generates its own labels and focus states;
  hand-written duplicate labels and pre-upgrade component classes are avoided.
- Checkboxes use `checkboxContainer > label.emby-checkbox-label > input + span`. The caption
  span must exist before attachment so Jellyfin can add its checkbox outline/icon safely.
- Dynamic inputs/selects use parsed native markup inside their containers: Jellyfin's v0
  `createdCallback` needs the parent to insert its native label. Attribute values are serialized
  through DOM setters before parsing, not interpolated untrusted HTML. Buttons/checkboxes use the
  host's v0 `createElement(tag, componentName)` signature, not the incompatible v1 options object.
- Sections/headings and data lists use `verticalSection`, `sectionTitle`, `paperList`,
  `listItem`, `listItemBody` and `listItemBodyText`. Buttons use the native raised/submit classes.
   File selection retains a labelled browser control with the native input class.
- Consent notices use a short **Allow deletion?** prompt, primary `button-submit` Yes,
  neutral raised No and a `paper-icon-button-light` Help control. Rules start collapsed;
  Help toggles `aria-expanded` without submitting anything. The notification dialog uses
  Jellyfin's `.dialog` surface, with only `color: inherit` to remove the browser dialog's
  default CanvasText; this supplies no replacement color scheme.
- Settings section navigation keeps the same controls instead of cloning/replacing them, preserving
  native component state, identity boundaries and drafts. Supported card extensions use native
  buttons without injecting a competing stylesheet or changing the host card's navigation.

These patterns were checked against Jellyfin Web **v12.2** component implementations. Skins
targeting normal Jellyfin component classes can style the same elements. This is compatibility
by using the host's patterns, **not a promise that every third-party skin, client or future DOM
change has been tested**. Stock Jellyfin 12.2 / Edge settings smoke tests cover generated labels,
dropdown/Help/draft behavior, keyboard focus and a 390px-wide layout; see [ACCEPTANCE.md](ACCEPTANCE.md).
Full keyboard/screen-reader, third-party-skin and companion-plugin acceptance remains required.
Server plugins cannot restyle unsupported native mobile/TV clients.

## Discord Activity boundary

Activity runs in Discord's isolated frame, not the Jellyfin Web document. It uses the shared
template and component-class vocabulary with a small standalone fallback stylesheet; it cannot
automatically load a Jellyfin skin or its custom component registry. Its adapter supplies
accessible labels for the fixed template without fetching host styles/scripts.

The explicit personal **Use this host's colors in Activity** action copies four validated
colors only. Arbitrary CSS, fonts, stylesheets, tokens and iframe credentials are never transferred.
Do not claim full skin inheritance in Activity or weaken origin/CSP/authentication to imitate it.

Upstream reference: [Jellyfin Web v12.2 elements](https://github.com/jellyfin/jellyfin-web/tree/v12.2/src/elements).
