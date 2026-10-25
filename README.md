# EXotica Floatube

A floating, always-on-top YouTube mini-player for Windows.

The video area is a **real Chromium browser** (Microsoft Edge WebView2 — the same
engine as Edge/Chrome, built into Windows), wrapped in a small borderless window
with its own playback controls. You sign in with your own Google account inside
the window, search for videos or playlists, and keep playback floating above
whatever else you're doing.

---

## Run it

1. Copy `EXoticaFloatube.exe` anywhere you like (it's a single self-contained file —
   no installer, no .NET needed).
2. Double-click it.
3. **First run note:** if your PC is missing the *WebView2 Runtime*, FloatTube
   will offer to open Microsoft's download page (it's a tiny ~2 MB install and
   is already preinstalled on Windows 11 and most Windows 10 PCs).

## Using it

| You asked for | How it works |
|---|---|
| Floats above other programs | Always-on-top by default; toggle with the 📌 button or "Always on top" in the ⋯ menu |
| Move it around | Drag anywhere on the top bar (double-click the bar to maximize) |
| Resize | Drag from ANY window edge or corner (the thin frame ring around the window is grabbable), the **◢** grip, or ⋯ → *Window size* presets |
| Tiny mode | Shrink below the old minimum (430×320, new minimum 200×160) and the YouTube UI zooms down with the window; grow back to restore 100% |
| Search inside the window | Type in the search box and hit Enter — or paste a YouTube video/playlist URL |
| Play / pause, skip | ▶⏸, ↺10 / ↻10 (±10 s), ⏮ / ⏭ for playlist previous/next |
| Change the timeline | Click or drag along the red timeline bar |
| Volume | Speaker button = mute, slider = volume. 🔁 loops the current video |
| Minimize | The — button (top bar) sends it to the taskbar |
| EX social | ⋯ → *EX social* — quick links to the EXotica Inc. community Discord, TikTok (@exotica1nc_) and YouTube channel |
| Support | ⋯ → *Support us 💚* — MoMo / VietQR donation QR (works from any Vietnamese banking app); *Support us ☕* — Ko-fi page, coming soon |
| Idle auto-hide | After 5 s without interacting, the toolbars hide leaving pure video; move the cursor back inside to bring them back (toggle in ⋯) |
| Click-through | ⋯ → *Disable interaction* — clicks pass straight through this window to the apps underneath; toggle with the global hotkey (default **Ctrl+Alt+F**, rebindable in that dialog; right-clicking the taskbar icon → Close always works as an escape hatch) |

YouTube's own keyboard shortcuts also work when the video area is focused:
`Space`/`K` play-pause, `J`/`L` ±10 s, `←`/`→` ±5 s, `M` mute, `F` fullscreen.

### Accounts (the "browser base + pick an account" part)

- Click **Account** in the top bar (or ⋯ → *Sign in / choose Google account*).
- Google will show its standard **account chooser** — pick the account you want.
- **OR**, if Google complains that the embedded browser "may not be secure":
  ⋯ → **Sign in via your installed browser** → pick Chrome / Edge / Brave.
  A separate browser window opens Google's sign-in page (Google never blocks
  your real browser) — finish signing in there, the window closes by itself,
  and the session is imported into the app automatically.
  NOTE: this only borrows the browser to sign in once — playback always uses
  Floatube's own embedded engine, so Brave/Chrome extensions (like Brave's
  ad blocking) do NOT carry over. Use the built-in ad blocking below instead.
- Add more accounts later with "Use another account" on the chooser page.

### Built-in ad blocking (⋯ → "Block YouTube ads")

On by default. It works in three layers: (1) stripping ad placements out of
the player's own data before the player reads it (the same "json-prune" idea
uBlock Origin uses), (2) a network blocklist for third-party ad / tracker
domains (doubleclick.net, googlesyndication.com, …), (3) hiding sponsored
cards and auto-clicking "Skip Ad". It deliberately does NOT block YouTube's
ad-completion beacons — blocking those makes the player re-serve ads.
Note: this is an arms race; ads stitched server-side into the stream itself
cannot be removed by any client-side blocker (only YouTube Premium removes
those).
- Your sign-in, subscriptions, watch history and playlists then work like in a
  normal browser, and **stay signed in** between launches (the profile lives in
  `%LOCALAPPDATA%\FloatTube\profile`).
- If Google ever calls the embedded browser "not secure", make sure
  ⋯ → *Compatibility mode (act like Chrome)* is ticked (it is by default).

### Privacy

Nothing is proxied through third-party servers. Traffic goes straight from this
app to YouTube/Google, exactly like a standalone browser would — the app just
uses its own isolated profile folder on your PC. To "log out of everything",
delete `%LOCALAPPDATA%\FloatTube\profile`.

## Build it yourself

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download):

```powershell
.\build-exe.ps1        # -> publish\FloatTube.exe
```

or manually:

```
dotnet publish FloatTube.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish
```

## Troubleshooting

- **Black window / won't start** → install the
  [WebView2 Runtime](https://go.microsoft.com/fwlink/p/?LinkId=2124703).
- **Something else broke** → a log is written to
  `%LOCALAPPDATA%\FloatTube\error.log`.
- **Weird sign-in or playback state** → close the app, delete
  `%LOCALAPPDATA%\FloatTube\profile`, relaunch.
