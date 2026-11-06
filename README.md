# switchkey-ai

[![Buy Me a Coffee](https://img.shields.io/badge/Buy%20me%20a%20coffee-support-FFDD00?logo=buymeacoffee&logoColor=black)](https://buymeacoffee.com/sashko2000)

**KeyboardSwitcher** is a Windows tray app that fixes text typed in the wrong keyboard layout and, optionally,
corrects, translates and rewrites selected text with a small language model running **locally** on the NPU, GPU or CPU
(Intel OpenVINO, AMD Ryzen AI, llama.cpp; optionally an NVIDIA card) - or, if you choose, with a network model
(OpenAI, Claude, Gemini, Ollama). Typed text is never stored; text is sent only to a network model you selected.

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
| Choose the model right in the assistant and chat windows (the same model is then used everywhere) | model list at the top | - |

| Tray menu | Assistant window |
|---|---|
| ![Tray menu](docs/screenshots/tray-menu.png) | ![Assistant window](docs/screenshots/assistant.png) |

- **Interface languages:** English, Russian, Ukrainian, Slovak, Spanish, Hindi (the Windows language by default).
- **Preferred layout:** when a word looks the same in several layouts (`vfvf` = «мама» in both Russian and Ukrainian),
  the app switches to the preferred one — unless the previous word was in the other language.
- **Technical words** (cmd, git, npm, exe, ssh and ~380 more) are recognized even though they are missing from everyday
  dictionaries. The list is in `src/KeyboardSwitcher/Resources/tech.txt` (one word per line; rebuild after editing).
- Only the layouts installed in Windows are used.
- **Code editors:** in the code editor of Visual Studio and VS Code (also Cursor, Windsurf) the layout is not changed
  automatically; their other fields (search, Solution Explorer, chat, terminal) keep auto-correction. The focused element
  is found with UI Automation (VS: `WpfTextView`, VS Code: Monaco `inputarea`); the log shows what was detected.
  Turn off in *Settings → Not in the code editor*. Ctrl+Alt+Z still converts a word by hand.
- **Chat:** history is kept in `%LOCALAPPDATA%\KeyboardSwitcher\chats` (one JSON file per chat; delete a chat in the window
  or the file). Long conversations are trimmed to the newest messages that fit the model's context (~1800 characters on
  the Intel NPU, ~10000 on GPU/CPU).

## Network models: OpenAI, Claude, Gemini, Ollama

*Settings → Network...* adds connections: provider, name, address, API key and model (*List* asks the server for its
models, *Check* sends a test request). They appear in the model lists of settings, the assistant and the chat, marked
"(network)".

| Provider | Address (default) | API | Key |
|---|---|---|---|
| OpenAI | `https://api.openai.com/v1` | Chat Completions | from platform.openai.com |
| Claude (Anthropic) | `https://api.anthropic.com/v1` | Messages | from console.anthropic.com |
| Gemini (Google) | `https://generativelanguage.googleapis.com/v1beta/openai` | OpenAI-compatible | from Google AI Studio |
| Ollama / OpenAI-compatible | `http://localhost:11434/v1` | Chat Completions | usually none (LM Studio, vLLM, llama-server... - change the address) |

- With a network model, the **selected text and chat messages are sent to that provider** (Ollama on this computer
  keeps them local). Layout auto-correction never uses a model. Pricing and data use are the provider's.
- API keys are stored in `settings.json` encrypted with Windows DPAPI (only this Windows user can decrypt them).
  A key is never sent over plain http to another computer.
- Network models get longer texts (up to 20,000 characters) and longer chat history than local ones.

## Running the model on NPU → GPU → CPU

Turn on **Use the model for selected text, the assistant and chat** in *Settings*. The app shows what it will download and how much, then installs
everything into `%LOCALAPPDATA%\KeyboardSwitcher`. The model is loaded on first use and unloaded after 10 idle minutes
(both configurable). *Run on* selects the device: **Automatic** (NPU → GPU → CPU) or a specific one; if it fails, the next
one in the order is used. The settings window lists the devices found on this computer.

| Processor | NPU | GPU | CPU |
|---|---|---|---|
| **Intel** | OpenVINO GenAI 2026.4.1 | OpenVINO, `GPU.0` (Intel graphics) | OpenVINO |
| **AMD** | [ryzenai-server](https://github.com/lemonade-sdk/ryzenai-server) (Ryzen AI, newest release ≥ 1.7.0) | llama.cpp b11936, Vulkan (Radeon) | llama.cpp |
| **NVIDIA card** (optional) | - | llama.cpp b11936, CUDA 12.4 | - |

**NVIDIA:** turn on *Use the NVIDIA card (partly, if the model doesn't fit)* (off by default). The card is then tried
first. If the whole model fits into its **free** video memory at load time (GGUF file + ~10% + 1 GB for the context),
it runs entirely on the card. Otherwise as many layers as fit go to the card and the rest are computed on the CPU
(partial offload; the layer sizes are read from the GGUF file, 1 GB is kept free). If not even a quarter of the layers
fits, the card is skipped and the usual order is used. The settings window shows how much of the selected model will be
on the card. Needs an NVIDIA driver 551.61 or newer; llama.cpp for CUDA and the CUDA libraries are ~0.6 GB. Without this
option the NVIDIA card is never used, also not through Vulkan.

Example for a 6 GB card: Qwen3.5-4B, Qwen3-4B and Qwen3-1.7B fit entirely; Qwen3-8B ~97%, Qwen3.5-9B ~85%,
Gemma 4 12B ~68%, Qwen3.8-27B ~29% of the model on the card.

Models (pick one in settings):

| Model | Intel (OpenVINO, INT4) | GGUF (llama.cpp: AMD, NVIDIA, GGUF-only on Intel) | AMD NPU (ONNX) | NVIDIA needs |
|---|---|---|---|---|
| Qwen3-8B (default) | 4.75 GB | Q4_K_M, 5.0 GB | Qwen2.5-7B, 8.2 GB | ~6.2 GB |
| Qwen3.5-9B | - | Q4_K_M, 5.7 GB | - | ~6.8 GB |
| Gemma 4 12B | - | Q4_K_M, 7.1 GB | - | ~8.3 GB |
| Qwen3.8-27B (large) | - | UD-Q4_K_M, 16.5 GB | - | ~17.9 GB |
| Qwen3-4B | 2.3 GB, GPU and CPU only (no NPU version) | Q4_K_M, 2.5 GB | Qwen2.5-3B, 3.8 GB | ~3.6 GB |
| Qwen3.5-4B | - | Q4_K_M, 2.7 GB | - | ~3.8 GB |
| Gemma 4 E4B | - | Q4_K_M, 5.0 GB | - | ~6.1 GB |
| Qwen3-1.7B | converted locally (~1.2 GB) | Q8_0, 1.8 GB | Qwen2.5-1.5B, 2.1 GB | ~2.9 GB |

- **GGUF-only models** (Qwen3.5, Gemma 4, Qwen3.8; GGUF files by [Unsloth](https://huggingface.co/unsloth)): OpenVINO
  publishes them only for its image+text pipeline, so on Intel they run in llama.cpp on the Arc GPU (Vulkan) or the CPU,
  without the NPU. Qwen3.8 exists only as a 27B model: for a 24 GB NVIDIA card or a computer with 32+ GB of memory.
- Mistral-7B and Phi-3.5-mini were removed after the model tests: they are weak in Russian, Ukrainian and Hindi.
- Qwen3-1.7B is not published for the Intel NPU, so it is converted on this computer: the app installs Python 3.12 (per user)
  and Intel's conversion tools (~3 GB).
- If the Visual C++ Runtime is too old, the app installs it (Windows asks for confirmation).
- AMD NPU: only XDNA 2 (Ryzen AI 300/400) with NPU driver 32.0.203.280 or newer. XDNA 1 (Ryzen 7040/8040/200) can't run
  language models and is skipped. If the NPU comes first, llama.cpp with a GGUF model is installed too, so the GPU/CPU
  can take over when the NPU fails.
- llama-server / ryzenai-server run as a separate process bound to 127.0.0.1; it is closed together with the app.

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
| `test.bat`, or **Test Explorer** in Visual Studio (`tests\KeyboardSwitcher.Tests`, MSTest) | layout detection (minimum accuracy per scenario, single-letter words, text conversion, preferred layout, Hindi) and model logic without a real model: prompts for all actions and languages, retries (untranslated text, transliteration), fallback for templates without a system role, text clean-up, device order NPU → GPU → CPU for Intel and AMD, NVIDIA (memory check, CUDA first, partial offload, GGUF header), GGUF-only models, installation plans, catalog and translations, the local server backend (fake server), chat (history trimming, saved chats), network models (requests and errors of each provider, fake HTTP) | anywhere, GitHub Actions |
| `test_detector.bat` | detailed layout detection report with error examples (`-v`) | anywhere |
| `tools\ModelTests\model_tests.bat` | every catalog model on every device of this computer (NPU, GPU, CPU, NVIDIA) with real requests: correction, translation to all 6 languages, every assistant action. Downloads missing models; writes `model_test_report.md` | a real Windows computer (Intel or AMD) |

`model_tests.bat` options: `--models qwen3-8b,qwen3.5-9b`, `--devices NPU,GPU,CPU,NVIDIA`, `--no-download`, `--quick` (4 tests per device).
By default all models except Qwen3.8-27B are tested (add it with `--models qwen3.8-27b`): up to ~35 GB of downloads
(Qwen3-1.7B is converted on Intel), several hours.
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
- The AMD GPU/NPU path, the NVIDIA (CUDA) path and the GGUF-only models have not been fully tested on real hardware yet.
- With Smart App Control on, the GGUF-only models and the NVIDIA card are not available (llama.cpp is unsigned).

## Project structure

`src/KeyboardSwitcher` — C#, .NET 10, plain Windows API (no WinForms):

| File | Purpose |
|---|---|
| `KeyboardMonitor.cs` | keyboard hook, word tracking, replacement |
| `CodeEditors.cs` | is the focus in a code editor (UI Automation) |
| `LayoutDetector.cs`, `Layouts.cs` | layout detection: frequency dictionaries + trigram model, key tables |
| `TrayApp.cs`, `SettingsWindow.cs`, `AssistantWindow.cs`, `ChatWindow.cs`, `NetModelsWindow.cs` | tray icon, menu, hotkeys, windows |
| `NetModels.cs` | network models: OpenAI / Anthropic / Gemini / Ollama requests, encrypted keys, model lists |
| `ChatStore.cs` | saved chats |
| `AiCorrector.cs` | prompts for correction, translation and rewriting |
| `AiPlatform.cs` | engine choice by processor vendor, NPU → GPU → CPU order |
| `OpenVinoGenAI.cs`, `LlmBackend.cs`, `AmdSupport.cs`, `NvidiaSupport.cs` | OpenVINO C API, local server backend, AMD detection and llama.cpp engines, NVIDIA cards (CUDA) |
| `AiSetup.cs` | downloads and installation |
| `Loc.cs`, `Resources/Strings.tsv` | interface translations |

`tests/DetectorTests` — layout detection tests on dictionary words.

## Support

If the app saves you time, you can [buy me a coffee](https://buymeacoffee.com/sashko2000) ☕

## License

[MIT](LICENSE). Third-party components and their licenses: [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
