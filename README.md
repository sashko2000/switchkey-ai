# switchkey-ai

[![build](https://github.com/sashko2000/switchkey-ai/actions/workflows/build.yml/badge.svg)](https://github.com/sashko2000/switchkey-ai/actions/workflows/build.yml)
[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-support-FFDD00?logo=buymeacoffee&logoColor=black)](https://buymeacoffee.com/sashko2000)

**KeyboardSwitcher** is a Windows tray app that:

- **fixes text typed in the wrong keyboard layout** as you type (`ghbdtn` → `привет`, `руддщ` → `hello`) - English,
  Russian, Ukrainian and Hindi, entirely offline, without any model;
- **corrects, translates and rewrites selected text, and chats** with a language model - a **local** one running on
  the NPU, GPU or CPU of your computer (Intel OpenVINO, AMD Ryzen AI, llama.cpp, optionally an NVIDIA card), or a
  **network** one you connect yourself (OpenAI, Claude, Gemini, Ollama).

Typed text is never stored. Text leaves the computer only if you choose a network model.

![Settings window](docs/screenshots/settings.png)

## Contents

- [Download](#download)
- [What it does](#what-it-does)
- [Keyboard layout auto-fix](#keyboard-layout-auto-fix)
- [Language model](#language-model)
- [Local models and devices](#local-models-and-devices)
- [Network models](#network-models)
- [Privacy and files](#privacy-and-files)
- [Limitations](#limitations)
- [Building and tests](#building-and-tests)
- [Project structure](#project-structure)

## Download

Get `KeyboardSwitcher-<version>-win-x64.exe` from [Releases](../../releases) and run it - no installation, no .NET
needed (Windows 10/11, x64). The tray icon «Aя» appears next to the clock; everything else is in its menu.

The model features are off until you turn them on in *Settings*; the app then shows what it will download and how much.

## What it does

| What | How | Model |
|---|---|---|
| Fix the layout of a word automatically (`ghbdtn` → `привет`, `ghbdsn` → `привіт`, `сьв` → `cmd`, `vcmdls` → `नमस्ते`) | after Space / Enter / Tab | - |
| Convert the last word / undo an automatic fix | **Ctrl+Alt+Z** | - |
| Convert the selected text to another layout | **Ctrl+Alt+X** | - |
| Correct the selected text (typos, wrong layout, capitalization, punctuation) | **Ctrl+Alt+A** | yes |
| Translate the selected text between two languages (Russian ↔ English by default) | **Ctrl+Alt+T** | yes |
| Assistant: translate to English, Russian, Ukrainian, Slovak, Spanish or Hindi; fix, more polite, formal, casual, shorter, simpler, explain, reply | tray menu | yes |
| Chat with the model; the selected text goes into the message field; chats are saved | **Ctrl+Alt+C** or tray menu | yes |

All hotkeys can be changed or turned off in *Settings*. The interface is in English, Russian, Ukrainian, Slovak, Spanish
and Hindi (the Windows language by default).

| Tray menu | Assistant window |
|---|---|
| ![Tray menu](docs/screenshots/tray-menu.png) | ![Assistant window](docs/screenshots/assistant.png) |

## Keyboard layout auto-fix

- Works on the CPU with frequency dictionaries and a letter-trigram model - no model, no network.
- Only the layouts installed in Windows are used: English (US), Russian, Ukrainian, Hindi InScript.
- **Preferred layout:** when a word is the same in several layouts (`vfvf` = «мама» in Russian and Ukrainian), the
  preferred one wins - unless the previous word was in the other language.
- **Technical words** (cmd, git, npm, exe, ssh and ~380 more) are recognized although everyday dictionaries lack them.
  The list is `src/KeyboardSwitcher/Resources/tech.txt` (one word per line; rebuild after editing).
- **Code editors:** in the code editor of Visual Studio and VS Code (also Cursor, Windsurf) the layout is not changed;
  their search boxes, Solution Explorer, chat and terminal keep the auto-fix. The focused element is found with UI
  Automation (Visual Studio: `WpfTextView`, VS Code: Monaco `inputarea`); the log shows what was detected.
  *Settings → Not in the code editor* turns it off.
- **Excluded programs:** password managers and remote desktop are excluded by default; add more in *Settings*.
- Undoing a fix with Ctrl+Alt+Z remembers the word, so it is not fixed again.

## Language model

Turn on **Use the model for selected text, the assistant and chat** in *Settings* and pick a model. The same model is
used everywhere; it can also be switched right in the assistant and chat windows. The model list shows which models are
installed (✓) and how much the others will download.

- A local model is loaded on first use and unloaded after 10 idle minutes (both configurable).
- Selected text: up to 1,500 characters for a local model, 20,000 for a network one.
- **Chat:** history is saved in `%LOCALAPPDATA%\KeyboardSwitcher\chats` (one JSON file per chat). Long conversations
  are trimmed to the newest messages that fit the model: ~1,800 characters on the Intel NPU, ~10,000 on a GPU or CPU,
  ~60,000 for a network model.

## Local models and devices

*Run on* in *Settings* selects the device: **Automatic** (NPU → GPU → CPU) or a specific one; if it fails, the next one is
used. The settings window lists the devices found on this computer.

| Processor | NPU | GPU | CPU |
|---|---|---|---|
| **Intel** | OpenVINO GenAI 2026.4.1 | OpenVINO (Intel graphics) | OpenVINO |
| **AMD** | [ryzenai-server](https://github.com/lemonade-sdk/ryzenai-server) (Ryzen AI, newest release ≥ 1.7.0) | llama.cpp b11936, Vulkan (Radeon) | llama.cpp |
| **NVIDIA card** (optional) | - | llama.cpp b11936, CUDA 12.4 or 13.4 | - |

Everything is downloaded into `%LOCALAPPDATA%\KeyboardSwitcher` in the background; the layout auto-fix keeps working
meanwhile. If the Visual C++ Runtime is too old, Microsoft's installer is started (Windows asks for confirmation).

### Models

| Model | Intel (OpenVINO, INT4) | GGUF (llama.cpp) | AMD NPU (ONNX) | NVIDIA, whole model |
|---|---|---|---|---|
| Qwen3-8B (default) | 4.75 GB | Q4_K_M, 5.0 GB | Qwen2.5-7B, 8.2 GB | ~6.2 GB |
| Qwen3.5-9B | - | Q4_K_M, 5.7 GB | - | ~6.8 GB |
| Gemma 4 12B | - | Q4_K_M, 7.1 GB | - | ~8.3 GB |
| Qwen3.8-27B (large) | - | UD-Q4_K_M, 16.5 GB | - | ~17.9 GB |
| Qwen3-4B | 2.3 GB, GPU and CPU only | Q4_K_M, 2.5 GB | Qwen2.5-3B, 3.8 GB | ~3.6 GB |
| Qwen3.5-4B | - | Q4_K_M, 2.7 GB | - | ~3.8 GB |
| Gemma 4 E4B | - | Q4_K_M, 5.0 GB | - | ~6.1 GB |
| Qwen3-1.7B | converted locally (~1.2 GB) | Q8_0, 1.8 GB | Qwen2.5-1.5B, 2.1 GB | ~2.9 GB |

- **GGUF-only models** (Qwen3.5, Gemma 4, Qwen3.8; files by [Unsloth](https://huggingface.co/unsloth)): OpenVINO
  publishes them only for its image+text pipeline, so on Intel they run in llama.cpp on the Arc GPU (Vulkan) or the CPU,
  without the NPU. Qwen3.8 exists only as a 27B model - for a 24 GB NVIDIA card or 32+ GB of memory.
- **Qwen3-1.7B** has no ready Intel NPU version, so it is converted on this computer: the app installs Python 3.12
  (per user) and Intel's conversion tools (~3 GB).
- **AMD NPU:** only XDNA 2 (Ryzen AI 300/400) with NPU driver 32.0.203.280 or newer. XDNA 1 (Ryzen 7040/8040/200) can't
  run language models and is skipped. llama.cpp is installed too, so the GPU/CPU can take over.
- llama-server and ryzenai-server run as a separate process bound to 127.0.0.1 and close together with the app.
- Mistral-7B and Phi-3.5-mini were removed after the model tests: they are weak in Russian, Ukrainian and Hindi.

### NVIDIA graphics card

Turn on *Use the NVIDIA card (partly, if the model doesn't fit)* - off by default; without it the NVIDIA card is never
used. The card is then tried first:

- the whole model goes to the card if it fits into the **free** video memory at load time (GGUF file + ~10% + 1 GB);
- otherwise as many layers as fit (sizes are read from the GGUF file, 1 GB is kept free), the rest runs on the CPU;
- if not even a quarter of the layers fits, the card is skipped.

The settings window shows how much of the selected model will be on the card; the status shows the layers, e.g.
`CUDA 13.4, 31 of 32 layers`. For a 6 GB card: Qwen3.5-4B, Qwen3-4B and Qwen3-1.7B fit entirely; Qwen3-8B ~97%,
Qwen3.5-9B ~85%, Gemma 4 12B ~68%. Blackwell cards (RTX 50, RTX PRO) with driver 580+ get the CUDA 13.4 build,
others CUDA 12.4 (driver 551.61+); ~0.6 GB download.

### Example speed

Intel Core Ultra 7 255H (Arc 140T, NPU), NVIDIA RTX PRO 500 Blackwell 6 GB; average time of short requests
(`tools\ModelTests`):

| Model | NPU | Arc 140T | CPU | NVIDIA |
|---|---|---|---|---|
| Qwen3-8B | 7.4 s | 2.4 s | 4.6 s | 1.4 s (35 of 36 layers) |
| Qwen3-4B | - | 1.7 s | 2.9 s | |
| Qwen3-1.7B | 2.5 s | 0.8 s | 1.5 s | |
| Qwen3.5-9B | - | ~6 s (Vulkan) | | 1.3-2.2 s (31 of 32 layers) |

## Network models

*Settings → Network...* adds connections: provider, name, address, API key and model. *List* asks the server for its
models, *Check* sends a short test request (can be cancelled; waits at most 45 s). Connections appear in all model
lists marked "(network)".

| Provider | Address (default) | API | Key |
|---|---|---|---|
| OpenAI | `https://api.openai.com/v1` | Chat Completions | platform.openai.com |
| Claude (Anthropic) | `https://api.anthropic.com/v1` | Messages | console.anthropic.com |
| Gemini (Google) | `https://generativelanguage.googleapis.com/v1beta/openai` | OpenAI-compatible | Google AI Studio |
| Ollama / OpenAI-compatible | `http://localhost:11434/v1` | Chat Completions | usually none (LM Studio, vLLM, llama-server - change the address) |

With a network model, the **selected text and chat messages are sent to that provider** (Ollama on this computer keeps
them local); its pricing and data policy apply.

## Privacy and files

- Typed text and keystrokes are never stored or sent. The layout auto-fix never uses a model.
- Model requests go to the local model, or to the network model you chose.
- API keys are stored encrypted with Windows DPAPI (only this Windows user can decrypt them) and are never sent over
  plain http to another computer.

| What | Where |
|---|---|
| Settings (network connections with encrypted keys) | `%APPDATA%\KeyboardSwitcher\settings.json` |
| Log (no typed text, no keystrokes) | `%LOCALAPPDATA%\KeyboardSwitcher\log.txt` |
| Chats | `%LOCALAPPDATA%\KeyboardSwitcher\chats` |
| Engines and models | `%LOCALAPPDATA%\KeyboardSwitcher` |

To remove everything, exit the app and delete these folders.

## Limitations

- Layouts: English (US), Russian (ЙЦУКЕН), Ukrainian, Hindi InScript (Windows "Hindi Traditional"). The Ukrainian ґ
  (AltGr+г) and the Hindi compound signs on the digit row (क्ष, त्र, ज्ञ) are not handled.
- Hindi: InScript only. Phonetic input and Hindi in Latin letters are left alone. The Hindi dictionary is small
  (~20k words), so short words that coincide with English ones (`Eh` = `आप`) are fixed only after a Hindi word.
- Doesn't work in windows of programs running as administrator (a Windows limitation).
- The app is unsigned and uses a keyboard hook: an antivirus or SmartScreen may warn.
- **Smart App Control** (Windows 11) blocks unsigned programs such as llama.cpp and ryzenai-server ("Bad Image",
  0xC0E90002). When it is on, AMD computers use OpenVINO on the CPU, and the GGUF-only models and the NVIDIA card are
  not available. It may also block `KeyboardSwitcher.exe` itself.
- The first NPU load takes ~30 s (later loads use a cache).
- Not fully tested on real hardware yet: AMD GPU/NPU, the network providers.

## Building and tests

- Visual Studio 2022 / Rider: open `KeyboardSwitcher.sln`.
- Or install the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`) and run `build.bat` → `dist\KeyboardSwitcher`
  (a single self-contained `KeyboardSwitcher.exe`). `build_zip.bat` also creates `dist\KeyboardSwitcher.zip`;
  `run.bat` starts the built app.
- Releases: pushing a tag `v*` builds the exe and zip and publishes a GitHub release.

| Tests | What they check | Where |
|---|---|---|
| `test.bat` or **Test Explorer** (`tests\KeyboardSwitcher.Tests`, MSTest) | layout detection (minimum accuracy per scenario), code-editor detection; model logic with fake models and servers: prompts, retries, chat, device order for Intel / AMD / NVIDIA, partial offload, installation plans, catalog and translations, network providers | anywhere, GitHub Actions |
| `test_detector.bat` | detailed layout detection report with error examples | anywhere |
| `tools\ModelTests\model_tests.bat` | every model on every device of this computer (NPU, GPU, CPU, NVIDIA) with real requests; writes `model_test_report.md` | a real Windows computer |

`model_tests.bat` options: `--models qwen3-8b,qwen3.5-9b`, `--devices NPU,GPU,CPU,NVIDIA`, `--no-download`, `--quick`
(4 tests per device). By default all models except Qwen3.8-27B are tested: up to ~35 GB of downloads, several hours.
`tools\KeyProbe` shows what the keyboard sends (useful for Apple keyboards); `tools\LangTest` checks translation quality.

## Project structure

`src/KeyboardSwitcher` - C#, .NET 10, plain Windows API (no WinForms):

| File | Purpose |
|---|---|
| `KeyboardMonitor.cs`, `Input.cs` | keyboard hook, word tracking, replacement |
| `LayoutDetector.cs`, `Layouts.cs` | layout detection: frequency dictionaries + trigram model, key tables |
| `CodeEditors.cs` | is the focus in a code editor (UI Automation) |
| `TrayApp.cs` | tray icon, menu, hotkeys, model jobs |
| `SettingsWindow.cs`, `AssistantWindow.cs`, `ChatWindow.cs`, `NetModelsWindow.cs` | windows |
| `AiCorrector.cs` | prompts for correction, translation, rewriting and chat |
| `AiPlatform.cs` | engine and device choice (Intel, AMD, NVIDIA, network) |
| `OpenVinoGenAI.cs`, `LlmBackend.cs` | OpenVINO C API, local server backend (llama-server, ryzenai-server) |
| `AmdSupport.cs`, `NvidiaSupport.cs` | AMD detection and llama.cpp engines; NVIDIA cards, CUDA, GGUF header |
| `NetModels.cs` | network models: OpenAI / Anthropic / Gemini / Ollama, encrypted keys |
| `AiSetup.cs` | model catalog, downloads and installation |
| `ChatStore.cs` | saved chats |
| `Loc.cs`, `Resources/Strings.tsv` | interface translations |

## Support

If the app saves you time, you can [buy me a coffee](https://buymeacoffee.com/sashko2000) ☕

## License

[MIT](LICENSE). Third-party components and their licenses: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
