# 🖼️ WeeGleek

**WeeGleek is a lightweight, 100% offline desktop image viewer built for speed and complete user privacy.** 

When you need a lightning-fast, no-nonsense browsing experience from an image viewer, but want peace of mind that your data never leaves your local machine, WeeGleek is built for you. 

🚫 No cloud dependencies.🚫 No telemetry or tracking. 🚫 No bloated features. 

Just your local image files, instantly.

<img width="850" height="610" alt="Gleek" src="https://github.com/user-attachments/assets/2e560ffa-c4f4-445d-a78b-a5000f53434d" />


---

## ✨ Key Features

* **Fast:** Optimized for near-instant launch times and seamless navigation through large directories of high-resolution images.
* **100% Offline:** Zero internet permissions requested or required. 
* **Absolute Privacy:** No tracking, no phone-home telemetry, and no account creation. Your data remains entirely yours.
* **Zero Bloat:** Rejects unnecessary features like cloud syncing, integrated social media sharing, or heavy editing suites to maintain a minimal resource footprint.
* **Familiar Control:** Intuitive keyboard shortcuts and mouse wheel controls inspired by classic, high-performance image viewers.

## ⌨️ Keyboard & Mouse Controls

| Action | Keys / Mouse |
| :--- | :--- |
| **Next / previous image** | `Right` / `Left`, `PageDown` / `PageUp`, `Space` / `Backspace`, Mouse Wheel |
| **First / last image** | `Home` / `End` |
| **Zoom** | `Ctrl` + Mouse Wheel (around the cursor), `+` / `-` |
| **Pan** | Click and drag |
| **Fit to window / actual size** | `F` or `0` / `1` |
| **Double-click** | Toggle fit ↔ 100% at the cursor |
| **Rotate clockwise / counter-clockwise** | `R` / `L` |
| **Fullscreen** | `F11` or `Enter` (`Esc` exits, or closes the window) |
| **Info bar** *(name, dimensions, size, zoom, position)* | `I` |
| **Open an image** | `Ctrl` + `O`, or drag and drop |


## 🛠️ Installation

WeeGleek is portable and requires no complex installation processes.

1. Download the latest release for your operating system from the **Releases** page.
2. Extract the archive to your preferred location.
3. Run `WeeGleek.exe` (Windows) or the binary executable for your specific platform.


## Building from Source (Windows)

You can build WeeGleek.exe using the native C# compiler built into Windows.
Run the included build.bat file to compile the source code in Program.cs:

## Why WebP Is Not Supported

WeeGleek sticks to the formats Windows' built-in GDI+ decoder handles (JPEG, PNG, GIF, TIFF, BMP). WebP was considered and left out for these reasons:

* **GDI+ cannot decode WebP.** WeeGleek uses `System.Drawing`, a thin wrapper over the GDI+ component that ships with Windows. GDI+ has no WebP decoder, so supporting WebP would mean adding a second one.
* **The usual way to add one conflicts with the single-executable design.** Bundling `libwebp` means shipping an extra native DLL or unpacking one at runtime. That breaks the one-file, standalone goal, and unpacking files at runtime can raise antivirus false-positive scores.
* **Security and patch management.** Image decoders parse untrusted input and are high-value exploit targets. `libwebp` had a heap buffer overflow in 2023 ([CVE-2023-4863](https://nvd.nist.gov/vuln/detail/CVE-2023-4863); rated Critical by Chromium, CVSS 8.8 High in NVD) that was exploited in the wild and fixed in libwebp 1.3.2. A DLL bundled with an app is not serviced by Windows Update: it stays at the version shipped until the maintainer tracks upstream advisories, rebuilds and publishes a release, and users install it.
* **Native code either way, but a different patch channel.** WeeGleek's own code is C# (memory-safe), but GDI+ is native code too and has had serious parsing bugs, from [MS04-028](https://docs.microsoft.com/en-us/security-updates/securitybulletins/2004/ms04-028) (2004) to [CVE-2025-53766](https://nvd.nist.gov/vuln/detail/CVE-2025-53766) (2025). The difference is servicing: GDI+ is patched through regular Windows updates; a bundled `libwebp` is not.
* **Alternatives were also considered.** Using a WebP codec installed on the PC (via the Windows Imaging Component) depends on a separately installed native codec that behaves differently across machines. A pure managed decoder would avoid native code, but would be a substantial new component to write, test and maintain.

## Other Unsupported Formats

* **EMF/WMF:** two of the 2025 GDI+ flaws were in metafile (EMF) record processing, so metafiles are a parser area to stay out of. 
* **SVG:** it can reference external resources, which clashes with “offline”, and rendering needs a big engine.
* **AVIF, HEIC, JPEG XL:** their decoders are huge, and the Windows route needs separately installed native codecs.

##  Contributing

Contributions are welcome! If you find a bug or have a suggestion to improve performance or core viewing functionality, please feel free to open an issue or submit a pull request. 

Please note that feature requests adding network connectivity, cloud storage, or heavy editing tools will be respectfully declined to keep the project true to its core philosophy.


## 📄 License

WeeGleek is open-source software licensed under the [MIT License](LICENSE).
