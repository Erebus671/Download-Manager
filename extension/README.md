# Download Solutions browser extension

Opt-in MV3 extension that hands large browser downloads to AtraTech Download Solutions through a Native Messaging host. All settings live in the app (Settings > Browser Integration). The extension's options page only shows them; the popup has quick toggles for enabling the extension and excluding the current site.

## Layout

| Path | Purpose |
|---|---|
| `src/background.js` | Watches downloads, pauses and hands off eligible ones, then cancels the browser copy once the app accepts it. Resumes the browser download if the app declines or can't be reached. |
| `src/popup.*` | Toolbar popup: status, enable toggle, exclude-this-site, open the app. |
| `src/options.*` | Read-only view of the app's settings. |
| `manifest.chromium.json` | Chrome, Edge, Brave, Opera, Vivaldi (MV3 service worker). Its `key` fixes the dev ID. |
| `manifest.firefox.json` | Firefox 128+ (background scripts, gecko ID). |

## Build

```
powershell -NoProfile -File tools/Build-Extension.ps1 [-IconDir <folder>]
```

Output in `publish/extension/`:

- `chromium/` and `firefox/`: unpacked builds.
- `download-solutions-chromium-<version>.zip`: GitHub release asset for **Load unpacked**; keeps the dev `key` so the host accepts its ID.
- `download-solutions-chromium-<version>-store.zip`: Chrome Web Store / Edge Add-ons package, with the dev `key` removed.
- `download-solutions-firefox-<version>-store.zip`: AMO package. Not a release asset: release Firefox only keeps Mozilla-signed add-ons.

Placeholder icons are generated unless `-IconDir` points at `icon-16/32/48/128.png`.

## Try it locally

1. Build and run the app from this repo, with the native host exe next to it (see Project-Notes, Reference 16). Turn on Settings > Browser Integration. On start, the app registers the host in HKCU for Chrome, Edge, Chromium, Brave and Firefox.
2. Chrome or Edge: open `chrome://extensions` (or `edge://extensions`), enable Developer mode, then use **Load unpacked** on `publish/extension/chromium`.
3. Firefox: open `about:debugging#/runtime/this-firefox` and use **Load Temporary Add-on** on `publish/extension/firefox/manifest.json`. Temporary add-ons are removed when Firefox restarts. Release Firefox only installs signed add-ons.

## IDs

| Item | Value |
|---|---|
| Native host | `com.atratech.downloadsolutions` |
| Chromium dev ID | `appmhpafdcnnfglmahilhiffokijfpjb` |
| Firefox ID | `downloadsolutions@atratech` |

Store IDs are added to `BrowserExtensionIds` once the listings exist.

## Rules

- Only `http`/`https` downloads from normal windows are taken over. Private and incognito windows are never taken over.
- A download is taken over when it's larger than the app's threshold (10 MB by default) or its size is unknown, unless its site is excluded.
- Cookies are sent only for the matching download. They are kept in the app's memory for that download and are never saved or logged.
- Keep source files ASCII-only.
