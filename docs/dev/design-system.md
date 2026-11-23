# The app's design system

One palette, type scale, spacing, focus and motion set for everything the app itself draws: the
Home screen and its tiles, the sky's chrome, the Explorer, Memories, Projects, Studio, the readers,
Sources & settings, and the frame around a dashboard. The Android launcher uses the same names
([Android](#android)).

The tokens are CSS custom properties on `:root` in [`app/src/index.css`](../../app/src/index.css).
Use them; do not write a new literal where a token fits. A check keeps the palette honest
([The palette check](#the-palette-check)).

**A dashboard's page is its author's.** What an author wrote inside a dashboard (its data, layout,
type, colours and density) is never restyled by these tokens. The page gets its own
`--rhythm-*` names from `app/src/dashboardFrame.ts`; those are a separate contract with
dashboard authors, with values of their own, and they do not change when these do. The app styles
only what it draws around a dashboard ([The dashboard frame](#the-dashboard-frame)).

## Tokens

### Surfaces, darkest first

| Token | Value | Job |
|---|---|---|
| `--ui-sky` | `#0b1015` | the page and the living sky |
| `--ui-night` | `#101a22` | a full view drawn over the sky (a panel, a reader) |
| `--ui-surface` | `#17252e` | cards, bars and sheets |
| `--ui-raised` | `#20303b` | a control or row lifted off a surface |
| `--ui-card` | `linear-gradient(126deg, #1c3037, #17252e 54%, #14212b)` | a card's fill |

### Lines

| Token | Value | Job |
|---|---|---|
| `--ui-edge` | `#435a64` | a card's own edge, at rest |
| `--ui-edge-strong` | `#8eafa9` | that edge under a pointer |
| `--ui-line` | `#415560` | a rule between regions; the border around a labelled control |
| `--ui-line-strong` | `#678490` | a boundary that alone marks a control (3:1 on every surface) |
| `--ui-divider` | `rgb(89 118 126 / 0.31)` | a quiet rule inside a surface: under a header, between rows |

### Text: three levels

| Token | Value | Job |
|---|---|---|
| `--ui-ink` | `#eef4f3` | the thing itself: names, titles and values |
| `--ui-soft` | `#b8c8cc` | its label, and reading text |
| `--ui-faint` | `#9cafb5` | its metadata: times, counts, sources. Nothing a person must read is fainter |

A screen reads in three levels, not ten: ink for the thing, soft for its label, faint for its
metadata. Hierarchy beyond that comes from size and weight, never from a fourth grey.

### Accents: each has one job

| Token | Value | Job |
|---|---|---|
| `--ui-mint` | `#a4daca` | opens something, or is on: an opening arrow, a link, an active mark |
| `--ui-mint-fill` | `#244039` | behind a chosen control |
| `--ui-mint-edge` | `#6a9589` | the border round a chosen control |
| `--ui-focus` | `#b5e7da` | the keyboard focus ring, and nothing else |
| `--ui-lilac` | `#cbb6ed` | discovery: an insight, something found |
| `--ui-lilac-fill` | `#292737` | behind a discovery |
| `--ui-amber` | `#e6c487` | stale data, always beside the words that say so |
| `--ui-alert` | `#e08a97` | real attention only: a failure, a reading out of range |

Mint is not a decoration colour. A word in mint says "this opens" or "this is on"; a word in
lilac says "this was found". Amber never stands alone: colour is never the only sign that data
is old.

### Measured contrast

Checked by the palette check. Each figure is the lowest over sky, night, surface, raised and both
ends of the card gradient.

| Text | Lowest ratio | Where |
|---|---|---|
| ink | 12.19 : 1 | on raised |
| soft | 7.87 : 1 | on raised |
| faint | 5.95 : 1 | on raised |
| mint, focus, lilac, amber, alert | 5.33 : 1 or more | alert on raised |

On a chosen control (`--ui-mint-fill`) ink, soft, faint and mint all stay above 4.5 : 1, and on a
discovery (`--ui-lilac-fill`) ink, soft, faint and lilac do. The focus ring and `--ui-line-strong`
are at least 3 : 1 against every surface.

### Data colours stay

Colours that mean something in the data are not tokens and do not change: health teal `#75d8b9`,
knowledge blue `#9ccfe0`, memory purple `#b6a6cc`, caution `#c1a66d`, the glucose zones, the
sport and sleep-stage colours and chart series. Replace one only when it is an accidental
near-copy of a token used for a token's job.

### Translucent variants

A token at lower strength is a mix, not a new literal:

```css
background: color-mix(in srgb, var(--ui-mint) 12%, transparent);
border-color: color-mix(in srgb, var(--ui-edge) 60%, transparent);
```

## Type

| Token | Size | Use |
|---|---|---|
| `--ui-text-eyebrow` | 11 px | a short uppercase eyebrow or a column head, never running text |
| `--ui-text-meta` | 12 px | times, counts, sources, freshness: the smallest running text |
| `--ui-text-small` | 13 px | labels, controls, secondary lines |
| `--ui-text-body` | 14 px | reading text in panels and lists |
| `--ui-text-title` | 15 px | a card's or a row's name |
| `--ui-text-lead` | 16 px | a reader's body text, a panel's lead line |
| `--ui-text-section` | 18 px | a section's heading |
| `--ui-text-page` | 21 px | a view's title |

- **Two families.** `--ui-display` (Manrope) for names, titles and key values; `--ui-font`
  (DM Sans) for everything read. A large focal value (a glucose reading, a sleep score) may go
  beyond the scale; it stays Manrope.
- **Nothing below 11 px.** Text a person must read is 12 px or more. 11 px is only for a short
  uppercase eyebrow or a table's column head.
- **Numbers and times** use `font-variant-numeric: tabular-nums`, so a column of times shares
  a right edge and a ticking clock does not jitter.
- **Uppercase eyebrows** keep modest tracking, about `0.08em`, never wide letter spacing.
- **Weights.** Reading text 400; labels 500; names and titles 600 to 650. Light weights (300 to
  350) only for a large value that is at least 28 px.
- **Sentence case** for names and titles as their author wrote them; one line with an ellipsis
  where space is short, never a clipped word with no ellipsis.

## Space and corners

Space is on a 4 px grid: `--ui-space-1` 4, `-2` 8, `-3` 12, `-4` 16, `-6` 24, `-8` 32.

- Related things (a value and its unit, a label and its control): 4 to 8 apart.
- Things in one group (rows in a list, buttons in a row): 12 to 16 apart.
- Regions (a panel's sections, the header and the content): 24 apart.

Corners: `--ui-radius-chip` 6 (a tag, a small badge), `--ui-radius-control` 8 (a button, a field,
a select), `--ui-radius-panel` 10 (a notice, a box inside a panel), `--ui-radius-card` 14 (a card,
a dashboard's frame). A pill (a segmented control's track, a round icon button) is the one
exception, at full rounding.

## Focus

- Every keyboard target shows a ring when it has keyboard focus: `--ui-focus-ring` (2 px, the
  focus colour) at `--ui-focus-offset` (3 px). `index.css` gives it to buttons, links, fields,
  selects, `summary` and anything with a `tabindex`.
- A ring is never removed without a replacement that is at least as visible (the sky's stars draw
  their own outline instead).
- A card that opens takes a 3 px ring inset 4 px (`outline-offset: -4px`), so it is not clipped by
  the card's neighbours.
- The one exception: a dashboard's page in the reader, a panel or full screen. Its sealed frame is
  a tab stop into the author's document, and the frame shows no ring of its own; what shows inside
  is the author's to draw.

## Touch

- A touch target measures 44 × 44 px or more (`--ui-target`), by padding or by a pseudo-element
  around a small glyph. A row in a list counts as a target when the whole row is pressable.
- Controls a person types into are 16 px on phones, so the browser does not zoom the page.
- Phones: the safe-area insets (`env(safe-area-inset-*)`) are kept clear, and nothing scrolls the
  page sideways at 320 px.

## Motion

- A change of colour, border or background takes 120 to 180 ms (`--ui-fast`, `--ui-calm`) with
  `--ui-ease`. Larger motion that exists already (the sky, panels growing out of their tiles)
  stays as it is.
- Everything honours `prefers-reduced-motion: reduce`: `App.css` turns animation and transitions
  off for the whole app when it is set, and the "Pause ambient motion" setting pauses the sky.

## Selection and state

- **Selection is never colour alone.** A chosen option also has a border, a fill or a mark, and
  it still reads with colour removed (greyscale). The shared pattern (`.segmented` in `App.css`):
  `--ui-mint-fill` behind, a 1 px border of `--ui-mint-edge`, the label in ink; an option not chosen has no fill and its label in
  soft. The weight stays the same, so choosing never shifts the row.
- **On and off** settings that are toggle buttons (`aria-pressed`) say "on" or "off" in their
  words and carry a mark when on.
- **Stale** data keeps its existing words; amber goes beside them, never instead of them.
- **Unavailable**: the existing message, its first line in ink, the rest in soft.

## Base elements

`index.css` wires the browser's own elements to the tokens, so a view starts from the system:

- the root's text (`--ui-ink`), background (`--ui-sky`) and family (`--ui-font`);
- the focus ring on every keyboard target;
- `accent-color` (mint) for checkboxes, radios and ranges; placeholders in `--ui-faint`;
- thin scrollbars in `--ui-line` on a clear track;
- the text selection, a mint mix behind ink.

`App.css` holds the shared controls: `.icon-button` (a round 44 px target), `.text-button`,
`.segmented` (a group of choices, the chosen one filled and bordered), `.tag`, `select`,
`.search-field` and `.state-notice`.

## The dashboard frame

What the app draws around a dashboard at Home and in the reader (the launcher's previews are
below):

```text
┌──────────────────────────────────────────────┐  card: --ui-card fill, 1 px --ui-edge, radius 14,
│ ◇ Station departures                      ↗  │  overflow hidden; --ui-edge-strong under a pointer
├──────────────────────────────────────────────┤  header: 54 px tall, 16 px inset (40 px and 12 px
│                                              │  on phones), a --ui-divider under it; a small mint
│          the author's page, untouched        │  glyph, the title in Manrope 650 at 15 px, one line
│                                              │  with an ellipsis; the mint opening arrow at the end
├──────────────────────────────────────────────┤  page: 12 px from the header and footer (8 px in a
│ Recorded 10:06. Data read 10:08              │  corner, 4 px on phones), 16 px from the sides (8 px
└──────────────────────────────────────────────┘  on phones); footer: the freshness text, 12 px,
                                                  soft, tabular digits, 16 px inset (12 px on
                                                  phones), a --ui-divider above it
```

- The whole card is the opening target and takes the focus ring (3 px, inset 4 px); the arrow is
  the cue, not a separate target.
- On a wall display (2200 px and wider, where the tiles and header also scale up) the title takes
  `--ui-text-page` (21 px) and the freshness line `--ui-text-section` (18 px), and the header and
  footer sit 24 px in from the edge; Home's built-in tiles do the same with their names and
  freshness lines. The header keeps its height, and the author's page keeps its inset.
- The title is the author's, as they wrote it: sentence case, not upper-cased.
- The footer gives each time its subject: when the measurements were **recorded** ("Recorded",
  or "Source day" for a daily figure) and when the dashboard's **data was read** ("Data read"). A
  card keeps to one recorded phrase, the newest ("Latest recorded") when its feeds differ; the
  reader's status line names each feed with its time. A date, its time and its zone stay on one
  line. A failed refresh says whether the last data read is still shown.
- Stale data: amber beside the existing words. Unavailable: the existing message, in soft with an
  ink first line.
- **On the launcher** (a tile of about 177 × 156 px) there is no card header around a preview:
  - A preview Rhythm draws from a dashboard's data (its summary, and its loading and unavailable
    states) draws its own one-line header: the title in Manrope 650 at 15 px, 8 px in from the
    tile's sides, and a 14 px mint opening arrow at the end; its mark goes first on a narrow tile or
    with enlarged text. Its footer says when the data was read ("Data read", the time, and the day
    when it is not today), with "partial" kept whole. The built-in glucose preview keeps its own
    small header ("Glucose" beside its mark).
  - Android's text size reaches the tile, so while either kind of drawn preview holds more words
    than its tile, it gives things up in turn (`usePreviewFit.ts`): its chart, a detail's kind (its
    date stays), its footer or coverage line, then a repeated or lesser line. Nothing overlaps or is
    cut.
  - A preview its author drew (`rhythm-launcher-preview`) is the author's whole card: it keeps its
    own rectangle and contents, and Rhythm draws nothing on it.
- **Rhythm owns** the title, the opening target, the boundary, the focus ring and the freshness
  line. **The author owns** everything between the header and the footer: data, layout,
  typography, colours and density. Nothing here restyles, restricts or validates an author's page.

## Old values and their tokens

The CSS used about 450 distinct colours, most of them near-copies. When you touch a rule, move its
literals to the token that does the same job:

| Old literal (and its near-copies) | Token |
|---|---|
| `#0b1015`, `#0a1217`, `#070c11` | `--ui-sky` |
| `#101a22`, `#0e171e`, `#0f1920`, `#111b23`, `#111a21` | `--ui-night` |
| `#17252e`, `#172229`, `#15222b`, `#162931` | `--ui-surface` |
| `#20303b`, `#25313a`, `#27373f`, `#accde41c`, `#93b4c31a` | `--ui-raised` |
| `#e4ecec`, `#e3f3f5`, `#e4eef0`, `#e3eef0`, `#e1ecef`, `#e9f2f2`, `#d7e5e9`, `#d6e6ec`, `#fff` (for text) | `--ui-ink` |
| `#d2dee3`, `#c9dbe1`, `#c4d4da`, `#c2d1da`, `#c0d4db`, `#b6cbd5`, `#b3c8d2`, `#b1c1cd`, `#a9bcc5` | `--ui-soft` |
| `#9fb4bf`, `#9daeb7`, `#91aab7`, `#8fa9b7`, `#8fa5b2`, `#8ca7b4`, `#83949d`, `#7e97a5`, `#718993`, `#6c828e` | `--ui-faint` |
| `#93b4c321`, `#93b4c31f`, `#93b4c31c`, `#93b4c317`, `#93b4c314`, `#91b3c322`, `#96b3c31e`, `#9db4c013` | `--ui-divider` |
| `#93b4c333`, `#7898ac2b`, `#95b8c333`, `#9bbbc255`, `#465b68` | `--ui-line` |
| `#93b4c35c`, `#90b9c555`, `#a7c9dc42` | `--ui-edge` |
| `#a6dccd`, `#a4daca`, `#99b9c8`, `#a8cddc`, `#8fd6c3` (a link or opening cue) | `--ui-mint` |
| `#95d2d9`, `#a6dccd88`, `#bedee8` (a focus ring) | `--ui-focus` |
| `#d6c38f`, `#d7bd83`, `#bdc5aa`, `#a8b897`, `#b3bf9e` (a stale or status word) | `--ui-amber` or `--ui-faint`, by job |
| `#e08a97`, `#e8b48f`, `#eaa3ad` (a failure) | `--ui-alert` |

A literal that is a data colour ([Data colours stay](#data-colours-stay)) keeps its value.

## Android

The native launcher carries the same palette under the same names in one Kotlin token object,
`Ui` in `UiTokens.kt` beside `ObservatoryStyle.kt`: the `--ui-` prefix dropped and the rest in
camelCase (`--ui-sky` is `Ui.sky`, `--ui-edge-strong` is `Ui.edgeStrong`, `--ui-mint-fill` is
`Ui.mintFill`, `--ui-text-eyebrow` is `Ui.textEyebrow`). The card's gradient is the brush
`Ui.card`, and Manrope 650 is `TitleWeight`. It
uses the same two bundled fonts (`ObservatoryBody`, DM Sans; `ObservatoryTitle`, Manrope), the
same type steps in sp with 12 sp the least readable text, the same spacing steps in dp, 48 dp
touch targets (Android's own least) and `ObservatoryEase`, which is `--ui-ease`. Installed apps
keep their own artwork, and the Sky, Glass and Original icon styles stay as shipped.

## The palette check

`app/tests/design-tokens.test.ts` reads the tokens from `index.css` and computes WCAG contrast for
every text and accent token on every surface, for text on the two fills, and for the focus ring
and `--ui-line-strong` against the surfaces. It fails when a pair a person reads falls under
4.5 : 1, or a ring or a control's boundary under 3 : 1. It runs with the app's tests
(`npm --prefix app test`).

## Adding a token

Add one only when the system needs a job no token does. Name it for its job, not its colour or
its place (`--ui-line-strong`, not `--ui-grey-3` or `--ui-settings-border`). Add it to `index.css`
with a one-line comment, to the tables here, to the palette check if it is text or a surface, and
to the launcher's token object.
