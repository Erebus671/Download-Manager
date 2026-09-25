# AtraTech Download Solutions

[![CI](https://github.com/Erebus671/Download-Manager/actions/workflows/ci.yml/badge.svg)](https://github.com/Erebus671/Download-Manager/actions/workflows/ci.yml)

A Windows download manager with a queue, pause/resume, and automatic retries.

## Features

- **Download queue**: add as many URLs as you want and set how many run at once.
- **Pause and resume**: pause one download or all of them. Resuming continues from where it stopped when the server supports it.
- **Automatic retries**: failed downloads retry on their own, waiting a little longer after each failure.
- **Survives restarts**: if you close the app mid-download, it asks you on the next launch whether to resume.
- **History**: completed, canceled, and failed downloads move to the History tab. You can retry them from there.
- **Built-in log panel**: shows what the app is doing, at a detail level you choose.
- **Dark theme**.

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

1. Paste a link into **URL**.
2. Pick a folder in **Save to** (or keep the default), then click **Add**.
3. Watch progress in the **Queue** tab. Each download has **Pause/Resume**, **Cancel**, and **Copy URL** buttons.
4. **Pause All** and **Resume All** control every active download at once.
5. Finished items appear in **History**. **Clear Completed History** removes them from the list. It doesn't delete the files.

If two downloads would save to the same filename, the app renames one of them so neither file gets overwritten.

## Settings

Open the **Settings** tab, change values, then click **Save Settings**.

| Setting | Default | What it does |
|---|---|---|
| Default download folder | Your `Downloads` folder | Where new downloads are saved |
| Max concurrent downloads | 2 | How many downloads run at the same time |
| Max retry attempts | 3 | How many times a failed download is retried before it's marked as an error |
| Minimum log level | Info | How much detail the log shows: Debug, Info, Warn, Error, Critical, or Fatal |

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
