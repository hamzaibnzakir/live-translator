<p align="center"><img src="static/icons/icon-128.png" width="88" alt="Brainbox"></p>

<h1 align="center">Brainbox Translate</h1>

<p align="center"><b>Read and reply on foreign-language Discord servers — and any website — as if everyone spoke English.</b><br>
On-device translation, rewritten in place in the page. Offline once a language pack is downloaded. No API keys, no accounts, no per-message network requests.</p>

---

Brainbox Translate is the browser sibling of the Windows app *Brainbox Live Translator* (screen OCR + overlay). This extension does something different: it rewrites the page's own text, the way Google Translate's "translate this page" does — no overlay, no OCR.

## What it does

| | |
|---|---|
| **Discord — read** | Every message in the channel turns into English inside Discord's own message elements. Mentions, emoji, links, code, spoilers, timestamps, usernames and markdown are untouched — only text nodes change. Hover a message for the original; **Alt+click** swaps it back. Replies' quoted lines, edits and embeds are handled; threads and forum *post pages* use the same message DOM (forum *list cards* are not translated yet). A tiny glow dot marks translated messages (can be turned off). |
| **Discord — reply** | Type in English, press **Ctrl+Shift+Y** (or the 🧠 pill above the message box). Your draft is rewritten into the channel's main language, with a *"they will read … / meaning, back in English …"* preview. **Ctrl+Z** (or the pill) restores your English. **You always press Enter yourself.** |
| **Quick translator** | Toolbar popup / side panel: paste anything → detected → English (or any language) → Copy. ⇄ swaps. **Ctrl+Enter** = translate & copy. Right-click a selection → *Translate selection to English* (card with Copy) or *Translate selection to…*. Optional clipboard mode (hotkey translates the clipboard in place). |
| **Any website** | Toolbar → *This page* → *Translate this page* / *Show original* (or **Alt+Shift+T**). Code, inputs, editors and `translate="no"` are skipped; content that loads later is followed; *Show original* restores the page exactly. *Always / Never translate this site*. |

## Install

1. Get the extension: download `brainbox-translate-<version>.zip` from the GitHub release / Actions artifact and unzip it — **or** build it: `cd browser-extension && npm ci && npm run build` (output in `dist/`).
2. Open **Chrome → Extensions** (`chrome://extensions`), turn on **Developer mode** (top right).
3. Click **Load unpacked** and pick the unzipped folder (or `dist/`). You can also drag the zip onto the page.
4. Pin Brainbox Translate in the toolbar (puzzle-piece icon → 📌).

> **Discord desktop app:** it's an Electron app and **cannot run browser extensions**. Use **discord.com in Chrome**. For an app-like window: open discord.com → ⋮ → *Cast, save and share* → *Install page as app*. The extension works inside that window.

Works in Chrome/Chromium 120+. In **Chrome 138+ desktop** it can also use Chrome's built-in on-device translator; in Edge, Brave, older Chrome and other Chromium browsers it uses the offline Bergamot engine.

## How it works

```
Discord / web page (content script, isolated world)
   │  MutationObserver (message ids, not CSS classes) + IntersectionObserver (visible → nearby → history)
   │  only Text.data is changed; originals kept per node; cached translations re-applied on re-mount
   ▼  chrome.runtime messages (one message block per request)
Service worker ──► Offscreen document = engine host (extension origin)
                    ├─ skip rules → language detection (Chrome LanguageDetector if ready, else ELD n-grams + script analysis, bundled)
                    ├─ cache: IndexedDB keyed (normalized text, from, to, engine) + memory LRU, size-capped
                    ├─ in-flight de-dupe (same text → one engine call), per-pair priority lanes, cancellation
                    └─ engines:  Chrome built-in Translator API (Chrome 138+)
                                 Bergamot WASM (Mozilla Firefox Translations models), one worker per pair
```

**Is the Chrome Translator API available in a content script?** Tested: yes — in Chromium 141 `Translator` and `LanguageDetector` exist in the content script's isolated world, in the offscreen document and in extension pages (`tests/e2e/chrome-builtin.spec.ts`). Brainbox still runs all engines in the offscreen document so the cache and models are shared by every tab, and starts Chrome pack downloads from an extension page (Chrome requires a user click for the first download of a pair).

**Language packs (Bergamot).** Mozilla's model registry is pinned in `src/engines/bergamot/registry.pinned.json` (116 English-hub pairs, ~58 languages, with the SHA-256 and size of every file — regenerate with `npm run pin-models`). Packs are downloaded only when you click *Download* (or if you enable auto-download), verified (compressed and decompressed hashes), stored in IndexedDB, and work offline afterwards. Non-English pairs pivot through English (fr→es = fr→en→es). Manage them in **Settings → Language packs** (sizes, download/remove, *Make fully offline*).

## Privacy & Discord's rules

- Nothing leaves your computer except (a) a pack download you start, (b) one message sent to **your own local** LM Studio/Ollama server when you click *✨ Polish with AI* (off by default).
- Brainbox **never** uses Discord's API, your token, self-bots or auto-sending. It only edits text on the screen you're looking at; you press Enter.
- Permissions: `storage`, `contextMenus`, `offscreen`, `sidePanel`, `clipboardWrite`, `scripting` + `activeTab` (inject "translate this page" only into the tab you click on), host access to `discord.com` and to Mozilla's model bucket. Other sites are optional host permissions granted per site ("Always translate this site"); `clipboardRead` is optional (clipboard mode only).

## Settings

Target language, engine (Auto / Chrome built-in / Bergamot), auto-download packs, detection confidence, pack manager, Discord (on/off, default per channel: auto / ask / never, remembered per server & channel, marker, hover, Alt+click, reply hotkey, back-translation preview), website lists, AI polish endpoint (`http://localhost:1234/v1` LM Studio or `:11434/v1` Ollama), clipboard mode, cache size + *Clear translation cache*.

## Tests (what's verified, honestly)

Run everything: `npm ci && npm run lint && npm test && npm run test:engine && npm run test:e2e && npm run perf` (E2E/perf need Chromium; `npx playwright install chromium` if you don't have it).

| Layer | What | Status in the delivered run |
|---|---|---|
| Unit (Vitest, 103 tests) | skip rules, detection wrapper, text-node walker, markdown & mention preservation, cache (memory + IndexedDB, LRU prune), priority queue, channel-language vote, Discord selector helpers, router (de-dupe, cancel, priorities, engine choice, needs-pack), scheduler, Chrome API wrapper (mocked), Bergamot downloader (hash/tamper) and lanes (fake worker) | ✅ all pass |
| Engine (real WASM + real models) | fr→en, ja→en, en→es, fr→es via pivot, known-phrase assertions, warm latency | ✅ all pass |
| E2E (Playwright, Chromium + unpacked extension) | Discord-like fixture built with **real React + real Slate** (virtualized list, Discord's ids/aria/data attributes, mentions, emoji, links, code, spoilers, replies, edits, embeds): FR/JA → EN with protected elements byte-identical; scroll away/back with zero new engine calls; edit re-translates; hover + Alt+click; reply hotkey → Slate **model** holds the translation (never a mixed state), preview + back-translation, undo, "sent" value = translation; popup both directions + copy; mixed-language page translate + late content + exact restore; context-menu card; pack manager; settings | ✅ 13 pass |
| Chrome built-in API | feature detection in all contexts | ⚠️ **detected but not runnable here**: availability is `downloadable`, but the CI/sandbox Chromium cannot download Google's on-device model, so a real Chrome-engine translation was **not tested**. The wrapper is unit-tested against a mocked API. |
| Performance | 500 messages, 3 packs | see below |

**Performance (local run, Linux CI-class machine, CPU only)**

| Metric | Result |
|---|---|
| New incoming message → English (warm pack) | **p50 77 ms, max 94 ms** (target < 300 ms ✅) |
| Page open, visible messages, warm packs | p50 ~0.7 s (content-script start + ~25 messages at once) |
| Page open right after model (re)load | p50 ~1.6 s — first use of a pack loads the model (0.5–4 s, once per session) |
| Catching up on history while scrolling | ~21 messages/s |
| Main-thread long tasks during the whole run | **0** |
| Content-script idle main-thread cost | ~12–20 ms per 3 s (polling noise of the probe itself included) |
| Page JS heap | ~7–11 MB |
| **Memory with packs in RAM** | ⚠️ **~0.5 GB per loaded Bergamot model** (fr-en 0.52 GB, ja-en 0.85 GB WASM heap; extension process RSS ~1.1 GB with two loaded). **This misses your ~150 MB target.** Measured: it is Marian's runtime allocation inside the Bergamot WASM build, per model, independent of workspace/batch settings; WebAssembly memory can't shrink. Mitigations shipped: max 2 models resident, a model is unloaded after 2 min unused, installed-but-unused packs cost 0 RAM, and Auto prefers Chrome's built-in engine (runs in Chrome's own service) when it's ready. |

CI (`.github/workflows/browser-extension.yml`) runs all of the above on every push touching `browser-extension/`, uploads the zip and a report with screenshots as artifacts, publishes the report to the `ci-results` branch (`browser-extension/REPORT.md`), and attaches the zip to a GitHub release for `ext-v*` tags.

## Manual checklist (real Discord) — please do this once

The fixture copies Discord's DOM, but real Discord changes often. What you should see:

1. **Load** — `chrome://extensions` → Developer mode → Load unpacked → the Brainbox card appears with no errors. Toolbar icon → popup opens.
2. **Packs** — popup → *Language packs* → search *French* → *Make fully offline*. Both columns show ✓ and a size (~26 MB each). If you're on Chrome 138+, the *Chrome built-in* column may offer *Download* too.
3. **Read** — open a French server's text channel on discord.com. Within a moment, messages read in English with a tiny violet/cyan dot. `@mentions`, emoji, links and code look exactly as before. Hover a message → card with the French original, "French · offline (Bergamot)". Alt+click → it turns back to French; Alt+click again → English. Scroll up into older history → it translates as it comes into view; scroll back down → no flicker.
4. **Reply** — the 🧠 pill above the message box shows `→ FR`. Type "Thanks, I'll join you tonight", press **Ctrl+Shift+Y**. The box now holds French; a panel shows *They will read* + the English back-translation. Press **Ctrl+Z** → English is back; press the hotkey again → French. Press **Enter yourself** → **check that the sent message is the French text** (this is the one thing the fixture can't fully prove for real Discord's editor). If the box doesn't change, tell me — it means Discord's editor needs a different input path (`src/content/discord/editor.ts`).
5. **Popup** — paste `Merci pour la partie !` → English appears → *Copy* → paste somewhere. Pick 🇫🇷 French as target, type English, **Ctrl+Enter** → French is copied.
6. **Any page** — open a French news site → popup → *This page* → *Translate this page* → it reads in English; *Show original* → the page is exactly back. Select a sentence → right-click → *Translate selection to English* → card with Copy.
7. If something on Discord isn't translated: run `localStorage.bbDebug = '1'` in the DevTools console of discord.com and reload — Brainbox logs what it requests. All Discord selectors live in `src/content/discord/discordSelectors.ts`.

## Limitations (honest list)

- **Discord desktop app** (Electron) is not supported; use discord.com in Chrome or Chrome's "install as app" window.
- **On-device models are weaker than an LLM**, especially with slang, gaming jargon and wordplay. Real example from our engine test: *"on se retrouve sur le serveur"* → *"we meet on the **waiter**"* (serveur = waiter). That's why the optional *✨ Polish with AI* (your local LM Studio/Ollama) exists.
- **Memory** — see the table above: ~0.5–0.85 GB per Bergamot model in RAM.
- **Chrome built-in engine** — implemented per Chrome's docs and unit-tested with a mock, but not exercised for real in automated tests (the test browser can't obtain the model). It exists only on desktop Chrome 138+ (not Edge/Brave/Firefox/mobile).
- **Formatting inside a sentence**: to keep sentences translatable as a whole, text split by **bold**/*italic* inside one sentence is put into the first text node, so that bit of styling can be lost in the translation (the original is restored exactly). Mentions/emoji/links/code split sentences into parts that are translated separately.
- **Alt+click** on a *translated* message is taken by Brainbox (Discord uses Alt+click for "mark unread"); it can be turned off in Settings.
- Very short messages ("salut", "ok") are only translated when the channel's language is already known — detection on 1–2 words is unreliable.

## Development

```
npm run build      # dist/ (production)        npm run watch   # rebuild on change
npm run lint       # ESLint + tsc --strict     npm test        # unit
npm run test:engine  # real Bergamot (downloads ~180 MB of test packs once, verified, cached in tests/.cache)
npm run test:e2e   # Playwright (builds dist-test/ = dist + localhost access + a test hook)
npm run perf       # 500-message performance run → reports/perf.json
npm run zip        # brainbox-translate-<version>.zip
npm run pin-models # re-pin Mozilla's model registry with SHA-256 of every file
```

Layout: `src/engines` (engine interface, Chrome, Bergamot, detection, cache, router), `src/offscreen` (engine host), `src/background` (service worker: routing, menus, commands, polish), `src/content/discord` (reader, composer, editor, **discordSelectors.ts**), `src/content/page` (any-website translator), `src/ui` (popup/side panel, settings), `tests/` (unit, engine, e2e with the Discord fixture, perf).

Third-party licenses: see [NOTICE](NOTICE).
