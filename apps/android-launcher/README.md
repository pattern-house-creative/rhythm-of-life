# Android launcher

A Home screen for an Android phone that puts your household's dashboards beside your apps. It is a
native Kotlin and Compose app that talks to a Rhythm of Life install you already run: live dashboard
previews sit in the app grid, the sky of stars is behind the screen, and today's insight is at the
bottom.

> **Development build** (`0.1.12-dev`). It has been used on one phone. There is no release build, no
> signing setup and no store listing: you build a debug APK yourself and install it. Read
> [What it does not do yet](#what-it-does-not-do-yet) first.

> **Not a medical device.** The launcher can show a glucose reading from your install. It is for your
> information only. Never make treatment decisions from it: use your device's own display and your
> care team for those.

## What you get

- **Your apps, as a launcher.** A four-column Home grid, a dock with six places and a drawer of every
  installed app. Apps open the usual way and keep working when your install cannot be reached.
- **Dashboard previews on Home.** A preview takes the room of four icons and redraws from fresh data.
  Touch it and the whole dashboard opens in a reader; Android's Back returns to Home.
- **One search.** Swipe up to search your apps, your dashboards and your install's stars together.
- **The sky.** "More sky" moves the grid down and shows the stars your install gives the launcher, with
  their links. Pinch, pan, touch a star to see what it is linked to, and open it.
- **Today's insight.** A strip at the bottom opens the day's insight with its points, sources and
  limitations. It is read-only here.
- **A built-in glucose card,** offered only when your install says it has one.

Hold a tile to move it, to switch a dashboard between an icon and a preview, or to take it off Home.
What you arrange is kept on the phone only; nothing is written to your install.

## What you need

- An Android phone on Android 9 or later.
- A Rhythm of Life install, `0.1.0-alpha.1` or later. Its app serves the launcher's pages
  (`launcher.html`, `launcher-card.html` and `launcher-companion.json`).
- **An HTTPS address for that install that the phone can reach.** The launcher refuses plain `http`.
  The [Tailscale recipe](../../deploy/exposure/tailscale/README.md) gives one inside your tailnet.
- To build it: Java 17 or later and the Android SDK with API 36.

## Build and install

From this folder, with `JAVA_HOME` and `ANDROID_HOME` set (or an ignored `local.properties` with
`sdk.dir`):

```sh
./gradlew :launcher:assembleDebug
```

On Windows PowerShell use `.\gradlew.bat`. The first build needs the internet: it downloads Gradle, the
packages the launcher builds with and the two fonts ([Fonts and licences](#fonts-and-licences)).

The APK is `launcher/build/outputs/apk/debug/launcher-debug.apk`, signed with your machine's debug key.
Install it with `adb install`, or copy it to the phone and open it. Android asks before it lets a new
app be the Home app; the launcher never changes that by itself.

## Connect it to your install

Open the launcher's settings and enter your install's address, such as `https://rhythm.<tailnet>.ts.net`.
It must be `https`, with no user name, path, query or fragment. Nothing about your install is built
into the APK: no address, no token and no pairing secret.

On the first connection Home gets up to two dashboard previews. After that you choose: "Add preview"
in the drawer, or hold an empty place on Home. A preview you remove stays removed.

"Disconnect and clear web session" in settings forgets the address and clears the launcher's cookies
and web storage.

## What a dashboard preview shows

Every saved dashboard gets a preview with nothing extra to save. The launcher's card page reads the
dashboard's first feed it understands and shows:

- for readings: the latest value or a labelled daily mean, with its unit and date, and a small chart;
- for a log: how many entries came back and the most recent one;
- for links: how many stars are linked, and their names;
- for a document: its title and a short excerpt.

A feed it does not understand shows "Open for details", and one that cannot be read is shown as
unavailable. A value that came back cut short is labelled partial; an old value is never shown as
current.

A dashboard can draw its own preview instead. Begin its saved HTML with exactly this element:

```html
<meta name="rhythm-launcher-preview" content="1">
```

Its `rhythm.onData` handler is then called with `mode: "card"`, `shape: "launcher"` and `edits: false`,
in the same sandboxed frame as every dashboard. Draw for about 177 by 156 CSS pixels: one useful
value, a short line of context and, if it helps, a small chart. If the page fails, or does not report
that it has drawn within eight seconds, the generated preview is shown instead, with a label saying so.

## How it is kept safe

- **Only your install's address.** The web views load that one HTTPS origin and nothing else. A
  request to anywhere else is answered empty, and a link or sign-in that leaves the origin is refused.
- **Certificate errors are refused.** There is no "continue anyway".
- **No bridge into Android.** Pages get no JavaScript interface, no file or content access, no
  downloads, no permission grants and no third-party cookies.
- **Two permissions:** internet access, and seeing the network's state. It lists the apps that have a
  launcher icon, to show them.
- **Little is kept.** The address, your layout and a few preferences, in the app's private storage.
  Android's backup and device transfer are turned off for it. Dashboards, stars and insights are held
  in memory only.
- **Screenshots are allowed.** Health data on a dashboard can therefore appear in a screenshot and in
  Android's recent-apps view.
- **Losing access closes it.** If your install answers 401 or 403, the launcher stops showing its
  data and points you to the sign-in in settings.

It uses the session your install gives its web app, through the web view's cookies. It holds no engine
token.

## What it does not do yet

- **No release build.** Only a debug APK that you build and sign yourself.
- **No sign-in that leaves your install's address.** A front door that sends you to another site to
  sign in, as Cloudflare Access does, is refused. An address you can reach without that, such as a
  tailnet one, works.
- **Tested mostly on an emulator.** The automated tests run on an emulator against a local stand-in
  for an install. On a real phone it has been used on one device, by hand.
- **No per-person sign-in.** It sees what your install's web app shows any browser at that address.
- **The sky is an overview.** It shows the stars the install returns for discovery, up to 2,000, not
  every record. Search finds the rest.

## Tests

```sh
./gradlew :launcher:testDebugUnitTest :launcher:lintDebug
```

runs the unit tests (layout, addresses the launcher must refuse, the sky's geometry) and Android lint.
CI runs these and builds the debug APK.

The emulator tests need an emulator you do not mind wiping, chosen by name:

```powershell
$env:ANDROID_SERIAL = 'emulator-5554'
.\gradlew.bat :launcher:connectedDebugAndroidTest
```

The tests of previews, the reader and the sky use a `fixture` build and a local server that stands in
for an install with made-up data. Build the web app first (`npm ci` and `npm run build` in `app/`), then:

```powershell
.\tests\prepare-fixture.ps1        # makes a seven-day certificate for the emulator, in build/
node tests/fixture-server.mjs      # serves https://127.0.0.1:9443 only
# in another terminal, with the emulator chosen as above:
.\gradlew.bat -PfixtureTests :launcher:connectedFixtureAndroidTest
```

Only the `fixture` build trusts that certificate, and only for the emulator's address of your
computer (`10.0.2.2`). It is a separate app (`io.github.pattern_house_creative.rhythm.launcher.fixture`).
Never install it on a phone you use.

## Fonts and licences

The launcher uses Manrope and DM Sans, both under the SIL Open Font License 1.1. They are not kept in
this repository. Each build fetches them from the commit of
[google/fonts](https://github.com/google/fonts) that `licenses/font-sources.json` pins, and refuses a
file whose SHA-256 differs from the one written there. Their licences are in `licenses/` and go into
the APK with the fonts.

The launcher's own code is under the repository's [Apache License 2.0](../../LICENSE). The packages it
builds with are listed in [docs/licenses/gradle.json](../../docs/licenses/gradle.json).
