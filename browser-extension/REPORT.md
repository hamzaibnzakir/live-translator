# Brainbox Translate — test report

Generated 2026-10-13T19:29:16.995Z for commit 52ebb277.

## Unit tests (Vitest)

**105 passed, 0 failed, 0 skipped** of 105.


## Engine tests (real Bergamot WASM + real models)

**7 passed, 0 failed, 0 skipped** of 7.


## End-to-end (Playwright, Chromium + unpacked extension)

| | Test | Project |
|---|---|---|
| ✅ | 500 messages: first visible translation, per-message latency, throughput, long tasks, memory | perf |

## Chrome built-in Translator API

```json
{
  "browserVersion": "141.0.7390.37",
  "mainWorld": {
    "Translator": "function",
    "LanguageDetector": "function"
  },
  "offscreen": {
    "chrome": "downloadable",
    "bergamot": "downloadable",
    "bergamotSize": 26234715,
    "detector": "downloadable"
  },
  "extensionPage": {
    "Translator": "function",
    "availability": "downloadable"
  },
  "testedAt": "2026-10-13T19:27:56.004Z"
}
```

## Performance (500 messages)

```json
{
  "messages": 500,
  "packsLoaded": [
    "fr-en",
    "ja-en",
    "es-en"
  ],
  "oneTimeModelLoadMs": 1043,
  "timeToFirstTranslatedMessageMs": 308,
  "firstTranslatedAfterFirstRowMountMs": 177,
  "visibleMessages": 6,
  "newMessageLatencyMs": {
    "n": 10,
    "p50": 64,
    "p95": 70,
    "max": 70,
    "all": [
      45,
      68,
      68,
      53,
      52,
      70,
      52,
      52,
      64,
      67
    ]
  },
  "warmVisibleMessageLatencyMs": {
    "n": 6,
    "p50": 460,
    "p95": 493,
    "max": 493
  },
  "coldVisibleMessageLatencyMs": {
    "p50": 560,
    "p95": 589,
    "max": 589
  },
  "history": {
    "messagesTranslated": 435,
    "messagesPerSecond": 26.6,
    "engineTexts": 287,
    "seconds": 16.3,
    "engineTextsPerSecond": 17.6,
    "engineCalls": 148,
    "cacheHits": 0
  },
  "longTasks": {
    "count": 0,
    "totalMs": 0,
    "maxMs": 0
  },
  "idleMainThreadBusyMsPer3s": 8,
  "memory": {
    "pageJsHeapMB": 8,
    "extensionProcessRssMB_before": 1595,
    "extensionProcessRssMB_with3packs": 1605,
    "wasmHeapReservedMB": [
      "fr-en:523",
      "ja-en:853",
      "es-en:522"
    ],
    "allChromiumRssMB": 2610
  }
}
```

## Screenshots

### discord-after

![discord-after.png](screenshots/discord-after.png)

### discord-before

![discord-before.png](screenshots/discord-before.png)

### discord-hover-original

![discord-hover-original.png](screenshots/discord-hover-original.png)

### discord-reply-preview

![discord-reply-preview.png](screenshots/discord-reply-preview.png)

### options-discord

![options-discord.png](screenshots/options-discord.png)

### options-packs

![options-packs.png](screenshots/options-packs.png)

### page-after

![page-after.png](screenshots/page-after.png)

### page-before

![page-before.png](screenshots/page-before.png)

### popup-en-fr

![popup-en-fr.png](screenshots/popup-en-fr.png)

### popup-fr-en

![popup-fr-en.png](screenshots/popup-fr-en.png)

### selection-card

![selection-card.png](screenshots/selection-card.png)

### v2-discord-lang-menu

![v2-discord-lang-menu.png](screenshots/v2-discord-lang-menu.png)

### v2-discord-notice-pill

![v2-discord-notice-pill.png](screenshots/v2-discord-notice-pill.png)

### v2-discord-reply

![v2-discord-reply.png](screenshots/v2-discord-reply.png)

### v2-discord-tooltip

![v2-discord-tooltip.png](screenshots/v2-discord-tooltip.png)

### v2-engine-check

![v2-engine-check.png](screenshots/v2-engine-check.png)

### v2-popup-pack

![v2-popup-pack.png](screenshots/v2-popup-pack.png)

### v2-popup

![v2-popup.png](screenshots/v2-popup.png)

### v2-sidepanel

![v2-sidepanel.png](screenshots/v2-sidepanel.png)
