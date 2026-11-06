# switchkey-ai

[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-support-FFDD00?logo=buymeacoffee&logoColor=black)](https://buymeacoffee.com/sashko2000)

**KeyboardSwitcher** is a Windows tray app that fixes text typed in the wrong keyboard layout and, optionally,
corrects, translates and rewrites selected text with a small language model running **locally** on the NPU, GPU or CPU
(Intel OpenVINO, AMD Ryzen AI, llama.cpp). Typed text is never stored or sent anywhere.

![Settings window](docs/screenshots/settings.png)

## Download

Get `KeyboardSwitcher-<version>-win-x64.exe` from [Releases](../../releases) and run it — no installation, no .NET needed.
The tray icon «Aя» appears next to the clock; settings are in its menu.

## Features

| What | How | Needs a model? |
|---|---|---|
| Auto-fix of the keyboard layout: English, Russian, Ukrainian, Hindi InScript (`ghbdtn` → `привет`, `ghbdsn` → `привіт`, `руддщ` → `hello`, `сьв` → `cmd`, `vcmdls` → `नमस्ते`) | automatic, after Space / Enter / Tab | no |
| Convert the last word / undo an auto-fix | **Ctrl+Alt+Z** | no |
| Convert the selected text to another layout | **Ctrl+Alt+X** | no |
| Correct the selected text (typos, wrong layout, capitalization, punctuation) | **Ctrl+Alt+A** | yes |
| Translate the selected text between two languages from settings (Russian ↔ English by default) | **Ctrl+Alt+T** | yes |
| Assistant: translate to English, Russian, Ukrainian, Slovak, Spanish or Hindi; fix, more polite, formal / casual, shorten, simplify, explain, reply | tray menu or Ctrl+Alt+T (window) | yes |
| Chat with the model; the selected text goes into the message field. Chats are saved on this computer | **Ctrl+Alt+C** or tray menu | yes |

| Tray menu | Assistant window |
|---|---|
| ![Tray menu](docs/screenshots/tray-menu.png) | ![Assistant window](docs/screenshots/assistant.png) |

- **Interface languages:** English, Russian, Ukrainian, Slovak, Spanish, Hindi (the Windows language by default).
- **Preferred layout:** when a word looks the same in several layouts (`vfvf` = «мама» in both Russian and Ukrainian),
  the app switches to the preferred one — unless the previous word was in the other language.
- **Technical words** (cmd, git, npm, exe, ssh and ~380 more) are recognized even though they are missing from everyday
  dictionaries. The list is in `src/KeyboardSwitcher/Resources/tech.txt` (one word per line; rebuild after editing).
- Only the layouts installed in Windows are used.
- **Chat:** history is kept in `%LOCALAPPDATA%\KeyboardSwitcher\chats` (one JSON file per chat; delete a chat in the window
  or the file). Long conversations are trimmed to the newest messages that fit the model's context (~1800 characters on
  the Intel NPU, ~10000 on GPU/CPU).

## Running the model on NPU → GPU → CPU

Turn on **text correction by the model** in *Settings*. The app shows what it will download and how much, then installs
everything into `%LOCALAPPDATA%\KeyboardSwitcher`. The model is loaded on first use and unloaded after 10 idle minutes
(both configurable). *Run on* selects the device: **Automatic** (NPU → GPU → CPU) or a specific one; if it fails, the next
one in the order is used. The settings window lists the devices found on this computer.

| Processor | NPU | GPU | CPU |
|---|---|---|---|
| **Intel** | OpenVINO GenAI 2026.4.1 | OpenVINO, `GPU.0` (Intel graphics) | OpenVINO |
| **AMD** | [ryzenai-server](https://github.com/lemonade-sdk/ryzenai-server) (Ryzen AI, newest release ≥ 1.7.0) | llama.cpp b11936, Vulkan (Radeon or any Vulkan GPU) | llama.cpp |

Models (pick one in settings):

| Model | Intel (OpenVINO, INT4) | AMD GPU/CPU (GGUF) | AMD NPU (ONNX) |
|---|---|---|---|
| Qwen3-8B (default) | 4.75 GB | Q4_K_M, 5.0 GB | Qwen2.5-7B, 8.2 GB |
| Qwen3-4B | 2.3 GB, GPU and CPU only (no NPU version) | Q4_K_M, 2.5 GB | Qwen2.5-3B, 3.8 GB |
| Qwen3-1.7B | converted locally (~1.2 GB) | Q8_0, 1.8 GB | Qwen2.5-1.5B, 2.1 GB |

- Mistral-7B and Phi-3.5-mini were removed after the model tests: they are weak in Russian, Ukrainian and Hindi.
- Qwen3-1.7B is not published for the Intel NPU, so it is converted on this computer: the app installs Python 3.12 (per user)
  and Intel's conversion tools (~3 GB).
- If the Visual C++ Runtime is too old, the app installs it (Windows asks for confirmation).
- AMD NPU: only XDNA 2 (Ryzen AI 300/400) with NPU driver 32.0.203.280 or newer. XDNA 1 (Ryzen 7040/8040/200) can't run
  language models and is skipped. If the NPU comes first, llama.cpp with a GGUF model is installed too, so the GPU/CPU
  can take over when the NPU fails.
- On AMD the model runs in a separate process (llama-server / ryzenai-server) bound to 127.0.0.1; it is closed together
  with the app.

Example speed (Qwen3-8B, Intel Core Ultra 7 255H): NPU 9.5 tokens/s, first token ~4 s; Arc 140T GPU 17 tokens/s, ~0.1 s;
CPU 13 tokens/s, ~0.6 s.

## Files

- Settings: `%APPDATA%\KeyboardSwitcher\settings.json`
- Log (no typed text, no keystrokes): `%LOCALAPPDATA%\KeyboardSwitcher\log.txt`
- Chats: `%LOCALAPPDATA%\KeyboardSwitcher\chats`
- Engines and models: `%LOCALAPPDATA%\KeyboardSwitcher`

## Building

- Visual Studio 2022 / Rider: open `KeyboardSwitcher.sln`.
- Or install the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`) and run `build.bat` → `dist\KeyboardSwitcher`
  (a single self-contained `KeyboardSwitcher.exe`; users don't need .NET). `build_zip.bat` also creates `dist\KeyboardSwitcher.zip`.
- `run.bat` starts the built app.

## Tests

| Command | What it checks | Where it runs |
|---|---|---|
| `test.bat`, or **Test Explorer** in Visual Studio (`tests\KeyboardSwitcher.Tests`, MSTest) | layout detection (minimum accuracy per scenario, single-letter words, text conversion, preferred layout, Hindi) and model logic without a real model: prompts for all actions and languages, retries (untranslated text, transliteration), fallback for templates without a system role, text clean-up, device order NPU → GPU → CPU for Intel and AMD, installation plans, catalog and translations, the local server backend (fake server), chat (history trimming, saved chats) | anywhere, GitHub Actions |
| `test_detector.bat` | detailed layout detection report with error examples (`-v`) | anywhere |
| `tools\ModelTests\model_tests.bat` | every catalog model on every device of this computer (NPU, GPU, CPU) with real requests: correction, translation to all 6 languages, every assistant action. Downloads missing models; writes `model_test_report.md` | a real Windows computer (Intel or AMD) |

`model_tests.bat` options: `--models qwen3-8b,phi-3.5-mini`, `--devices NPU,GPU,CPU`, `--no-download`, `--quick` (4 tests per device).
Testing all models downloads up to ~15 GB (Qwen3-1.7B is converted on Intel) and takes 1-3 hours.
- `tools\KeyProbe` shows what the keyboard sends (useful for Apple keyboards); `tools\LangTest` checks how well the
  model translates between languages.

## Limitations

- Layouts: English (US), Russian (ЙЦУКЕН), Ukrainian, Hindi InScript (Windows "Hindi Traditional"). The Ukrainian ґ
  (AltGr+г) and Hindi compound signs on the digit row (क्ष, त्र, ज्ञ) are not handled.
- Hindi: InScript only (checked at startup). Phonetic input (Hindi Phonetic, Google Input Tools) and Hindi written in
  Latin letters are left alone. The Hindi dictionary is small (~20k words), so short words that coincide with English
  ones (`Eh` = `आप`) are fixed only after a Hindi word.
- Doesn't work in windows of apps running as administrator (a Windows limitation).
- An unsigned app with a keyboard hook may trigger an antivirus or SmartScreen warning.
- Model correction: up to 1500 characters at a time; the first NPU load takes ~30 s, later loads are faster (cache).
- **Smart App Control** (Windows 11) blocks unsigned programs such as llama.cpp and ryzenai-server ("Bad Image",
  0xC0E90002). When it is on, AMD computers use OpenVINO (signed by Intel) on the CPU instead. The app itself is unsigned
  too, so Smart App Control may also block `KeyboardSwitcher.exe` downloaded from Releases.
- The AMD GPU/NPU path has not been fully tested on real AMD hardware yet.

## Project structure

`src/KeyboardSwitcher` — C#, .NET 10, plain Windows API (no WinForms):

| File | Purpose |
|---|---|
| `KeyboardMonitor.cs` | keyboard hook, word tracking, replacement |
| `LayoutDetector.cs`, `Layouts.cs` | layout detection: frequency dictionaries + trigram model, key tables |
| `TrayApp.cs`, `SettingsWindow.cs`, `AssistantWindow.cs`, `ChatWindow.cs` | tray icon, menu, hotkeys, windows |
| `ChatStore.cs` | saved chats |
| `AiCorrector.cs` | prompts for correction, translation and rewriting |
| `AiPlatform.cs` | engine choice by processor vendor, NPU → GPU → CPU order |
| `OpenVinoGenAI.cs`, `LlmBackend.cs`, `AmdSupport.cs` | OpenVINO C API, local server backend, AMD detection and engines |
| `AiSetup.cs` | downloads and installation |
| `Loc.cs`, `Resources/Strings.tsv` | interface translations |

`tests/DetectorTests` — layout detection tests on dictionary words.

## Support

If the app saves you time, you can [buy me a coffee](https://buymeacoffee.com/sashko2000) ☕

## License

[MIT](LICENSE). Third-party components and their licenses: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
