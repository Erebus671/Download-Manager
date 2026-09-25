# AtraTech Download Solutions

[![CI](https://github.com/Erebus671/Download-Manager/actions/workflows/ci.yml/badge.svg)](https://github.com/Erebus671/Download-Manager/actions/workflows/ci.yml)

A Windows download manager with a queue, pause/resume, automatic retries, and downloads that sort themselves into the right folders.

## Features

- **Download queue**: add as many URLs as you want and set how many run at once.
- **Pause and resume**: pause one download or all of them. Resuming continues from where it stopped when the server supports it.
- **Automatic retries**: failed downloads retry on their own, waiting a little longer after each failure.
- **Sorted by file type**: videos, pictures, music, and documents go to your Windows **Videos**, **Pictures**, **Music**, and **Documents** folders; everything else goes to **Downloads**. Folders and file types are editable, and sorting can be turned off.
- **Custom destination rules**: send matching downloads to a folder of your choice. Match on extension, file name, site, size, or file type. Folder paths can use `{site}`, `{date}`, `{category}`, and environment variables such as `%USERPROFILE%`.
- **Real file names**: downloads use the name the server provides, and **Rename** changes it, even mid-download. Existing files are never overwritten; the new file gets a number instead, e.g. `report (1).pdf`.
- **Signed automatic updates**: the app checks GitHub Releases, downloads updates in the background, and asks before installing. Every installer is verified against the AtraTech release signature first. Active downloads pause for the update and resume on their own afterward.
- **Survives restarts**: if you close the app mid-download, it asks you on the next launch whether to resume.
- **Single instance**: opening the app again, or launching it with a URL, hands off to the window that's already running.
- **History**: completed, canceled, and failed downloads move to the History tab. You can retry them from there.
- **Built-in log panel**: shows what the app is doing, at a detail level you choose.
- **Dark theme**.

## Roadmap

Plans can change. Items are listed in the order they're expected to ship.

### Next: v1.3.0 "The Assimilation Update"

- [ ] **Browser integration (opt-in)**: extensions for Chrome, Edge, and Firefox hand downloads over 10 MB to the app. Per-site exclusion list; browser cookies used only for the matching authenticated download.
- [ ] **Formatted release notes**: the update prompt shows headings, bold text, code, and bullets instead of raw Markdown.
- [ ] **Installer cleanup**: resolve the remaining harmless build warnings.

### Later

- [ ] **Extension store listings**: publish the browser extensions to the Chrome Web Store, Edge Add-ons, and Firefox Add-ons.
- [ ] **Video sites**: download from YouTube, public Instagram and TikTok content, and Twitch past broadcasts and clips. MP4 by default, MKV available. Helper tools (yt-dlp, ffmpeg, Deno) are downloaded on first use and checked against pinned SHA-256 hashes.
- [ ] **Twitch live recording (opt-in)**: record live streams with elapsed time and size in the queue; stream info is saved alongside the video.
- [ ] **Audio/video conversion**: ffmpeg presets for converting finished downloads.
- [ ] **Code-signed installer**: Authenticode signing so Windows SmartScreen stops warning on install.

### Shipped

- [x] **v1.2.0 "The Obsessive Compulsive Update"**: category folders, custom destination rules, **Save to** list, server file names, **Rename**, **Save Settings** confirmation.
- [x] **v1.1.0**: signed automatic updates, auto-resume after an update, single instance with URL handoff.
- [x] **v1.0.0**: queue, pause/resume, retries, history, resume prompt, log panel, dark theme, installer.

## Requirements

- 64-bit Windows 10 or 11
- Administrator rights to install
- .NET 10 Desktop Runtime. The installer adds it automatically if it's missing.

## Install

1. Download `AtraTechDownloadSolutions.exe` from the [Releases](https://github.com/Erebus671/Download-Manager/releases) page.
2. Run it and accept the Windows administrator prompt.
   - The installer isn't code-signed yet, so Windows SmartScreen may show **"Windows protected your PC"**. Click **More info**, then **Run anyway**.
3. Choose an install folder.
4. Choose where you want shortcuts: Desktop, Start Menu, or both.
5. Finish the wizard. Launch the app from the shortcut.

## Using the app

1. Paste a link into **URL** and press **Enter** or click **Add**.
2. **Save to** picks the destination: **Automatic (by file type)** (the default), a specific category, or **Choose folder...**.
3. Watch progress in the **Queue** tab. Each download has **Pause/Resume**, **Cancel**, **Copy URL**, and **Rename** buttons.
4. **Pause All** and **Resume All** control every active download at once.
5. Finished items appear in **History**. **Clear Completed History** removes them from the list. It doesn't delete the files.

## Settings

Open the **Settings** tab, change values, then click **Save Settings**. A message next to the button confirms the save or explains what needs fixing.

### General

| Setting | Default | What it does |
|---|---|---|
| Sort downloads into folders by file type | On | Sends each download to its category folder. When off, everything goes to the Other files folder. |
| Category folders | Windows **Videos**, **Pictures**, **Music**, **Documents**, and **Downloads** (Other files) | Where each category saves. **Edit file types...** changes which extensions belong to each category. |
| Custom destinations | None | Rules checked top to bottom before file-type sorting; the first match wins. Rule and file-type edits save immediately. |
| Max concurrent downloads | 2 | How many downloads run at the same time (1-10) |
| Max retry attempts | 3 | How many times a failed download is retried before it's marked as an error (0-20) |
| Minimum log level | Info | How much detail the log shows: Debug, Info, Warn, Error, Critical, or Fatal |

### Updates

| Setting | Default | What it does |
|---|---|---|
| Check for updates automatically | On | Checks GitHub Releases for a newer version |
| How often | At launch and every 24 hours | Also: at launch only, every 6 hours, or weekly |
| Download updates automatically | On | Downloads the update in the background. The app always asks before installing. |

**Check now** runs a check immediately.

## Where your data is stored

| What | Location |
|---|---|
| Queue, history, and settings | `%LOCALAPPDATA%\DownloadManagerApplet\state.json` |
| Log files | `%LOCALAPPDATA%\DownloadManagerApplet\logs\` |

When reporting a problem, set **Minimum log level** to **Debug**, reproduce it, and attach the latest log file.

## Uninstall

Open **Settings > Apps > Installed apps** (or **Control Panel > Programs and Features**), select **AtraTech Download Solutions**, and choose **Uninstall**. This removes the program and its shortcuts. Your downloaded files and the data folder above are left in place.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows.

```powershell
dotnet build src/DownloadManagerApplet/DownloadManagerApplet.csproj
```

The test project (`tests/`) is maintainer-only. It depends on a private package, so it won't restore outside the maintainer's environment. Build the app project directly as shown above.

To publish the app with a custom icon, pass the path to your `.ico` file. Without it, the app uses the default Windows icon.

```powershell
dotnet publish src/DownloadManagerApplet/DownloadManagerApplet.csproj -c Release -r win-x64 --self-contained false -o publish/win-x64 -p:AppIconPath="C:\path\to\icon.ico"
```

The installer is built with [Advanced Installer](https://www.advancedinstaller.com/) from `installer/AdvancedInstaller/AtraTechDownloadSolutions.aip`.

## License

The source code is released under the [MIT License](LICENSE).

The AtraTech name, logo, and icon artwork are not covered by that license and may not be used without permission.
