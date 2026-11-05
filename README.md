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


##  Contributing

Contributions are welcome! If you find a bug or have a suggestion to improve performance or core viewing functionality, please feel free to open an issue or submit a pull request. 

Please note that feature requests adding network connectivity, cloud storage, or heavy editing tools will be respectfully declined to keep the project true to its core philosophy.

## False Positive from a Virus Checker
Reasons the WeeGleek.exe binary might produce a false positive result from a virus checker. 

The compiled file  has no version info, a company or product name, an icon or a manifest, and the exe is unsigned and brand new. ML engines score that kind of anonymous binary higher, and I think it’s the biggest factor for giving a false positive alert.

The mouse-wheel router. WheelRouter imports WindowFromPoint and SendMessage and installs an app-wide message filter. It only forwards wheel messages between WeeGleek’s own panes, but statically it resembles input-interception tools. It’s probably the most suspicious-looking piece of code, and it’s only a convenience feature.

File-manager behavior. Enumerating drives, copying folders, moving, renaming, deleting and reading or writing the clipboard file list overlap with ransomware and clipper heuristics at the API level. That comes with being a file manager.

Small native imports. Calls into dwmapi, uxtheme, shlwapi and DPI settings (dark title bar, theme, sorting) are harmless but add to the count.

I check my computer regularly for viruses but if in doubt compile the source code yourself from the build.bat file.


## 📄 License

WeeGleek is open-source software licensed under the [MIT License](LICENSE).
