<p align="center"><img src="src/Translumo/Resources/Brainbox/brainbox.png" width="96" alt="Brainbox"></p>

<h1 align="center">Brainbox Live Translator</h1>

<p align="center"><b>A live translation layer for Windows.</b> Launch it once. Any foreign text that appears anywhere on any screen is translated into English (or your language) in place — no region selection, no hotkey, no screenshots.</p>

---

## How it works

```
Desktop (all monitors, current virtual desktop)
   │  GDI or DXGI Desktop Duplication capture, 2–4×/s
   ▼
Tile change detection (64-bit hash + luminance thumbnail per 32 px tile)
   │  only changed areas; video/games capped by "max wait"; caret/clocks cooled down
   ▼
Windows OCR on changed regions (every installed script: Latin, Japanese, Chinese, Korean, …)
   ▼
Tracking: same text keeps its identity when it moves; vanished text drops its overlay
   ▼
Filter: English / URLs / e-mail / paths / code / IDs / numbers / shortcuts / our own overlay → skipped
   ▼
Cache (memory LRU + SQLite via Windows' built-in winsqlite3) ── hit ──┐
   │ miss                                                          │
   ▼                                                               │
Context-aware batch translation (previous lines + nearby text)      │
LM Studio → Ollama → any OpenAI-compatible → (optional) Google      │
   │  circuit breaker + back-off; LM Studio closed ≠ crash          │
   ▼                                                               ▼
Smart overlay placement (glyph-sized, colour-matched, collision-free) → overlay window per monitor
   (excluded from capture, never focused, clipped to its text; hover a translation to peek at the original)
```

The overlay is **excluded from screen capture** (`WDA_EXCLUDEFROMCAPTURE`), so Brainbox can never read its own translations. As defence in depth it also ignores text matching what it is currently displaying.

## Requirements

- Windows 10 2004 (build 19041) or newer, or Windows 11, x64
- A Windows OCR language for every script you want to read — Settings → Performance → *Install OCR language* (e.g. `ja-JP`), or Windows Settings → Time & language → Language → add the language with "Optical character recognition".
- A translation engine:
  - **LM Studio** (default): load a chat model, then *Developer → Start Server* (`http://localhost:1234/v1`). Good small picks: Qwen2.5 7B/14B Instruct, Gemma 3, Llama 3.1 8B.
  - **Ollama**: `ollama pull qwen2.5:7b` (endpoint `http://localhost:11434/v1`).
  - Any OpenAI-compatible server or cloud key, the Translumo cloud LLM profiles, or Google Translate (online).

The model is never hard-coded: leave *Model* empty to use whatever your server has loaded.

## Using it

| | |
|---|---|
| Tray icon | Live on/off, target language, engine, mode, glow, pause, settings, restart, exit |
| `Ctrl+Alt+B` | Toggle live translation |
| `Ctrl+Alt+P` | Pause / resume |
| `Ctrl+Alt+G` | Toggle the glow |
| Hover a translation | Peek at the original underneath; clicks go to the app |

Modes: **Balanced** (default, 2 checks/s), **Performance** (4/s), **Battery Saver** (every 1.5 s), **Maximum Accuracy** (upscaled OCR, periodic full re-scan, optional vision-model fallback).

Settings, logs and the translation cache live in `%LOCALAPPDATA%\BrainboxLiveTranslator`.

The original Translumo region translator is still available from the tray (*Classic region translator*).

## Building and testing

```
dotnet build BrainboxLiveTranslator.sln -c Release
dotnet test src/Brainbox.Core.Tests          # engine unit + pipeline tests (run anywhere)
dotnet publish src/Translumo/Translumo.csproj -c Release -o publish -p:SkipBinariesExtract=true
publish\BrainboxLiveTranslator.exe --selftest --out selftest-out          # real-screen end-to-end test
publish\BrainboxLiveTranslator.exe --selftest --engine ollama --model qwen2.5:1.5b
```

The self-test opens a page with French, Japanese and English text on the real desktop and checks — from the outside, by capturing and OCR'ing the screen and sending real mouse/keyboard input — automatic detection, placement, English skipping, capture exclusion, click-through, window-follow, caching, text change/disappearance, engine outage + recovery, hotkeys, glow, startup registration, virtual desktops, GPU capture, and records CPU/RAM/GPU/latency. CI (`.github/workflows/brainbox-ci.yml`) runs all of it on a Windows desktop for every push and publishes the report to the `ci-results` branch.

## Project layout

```
src/Brainbox.Core            platform-neutral engine (no NuGet deps): Detection, Text, Translation,
                             Caching, Tracking, Overlay layout, Pipeline (ScreenWatcher), Settings
src/Brainbox.Core.Tests      xUnit tests incl. synthetic-desktop pipeline tests
src/Translumo/Brainbox       Windows layer: Capture (GDI/DXGI), Ocr (Windows OCR), Overlay (overlay + glow),
                             Shell (controller, tray, hotkeys, startup, virtual desktops), UI, SelfTest
src/Translumo*               original Translumo-LLM projects (classic translator, cloud translators)
```

## License

Apache-2.0. Built on [Translumo-LLM](https://github.com/HenryNebula/Translumo-AI) and [Translumo](https://github.com/ramjke/Translumo); see [NOTICE](NOTICE).
