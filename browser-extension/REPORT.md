# Brainbox Translate — test report

Generated 2026-10-14T10:00:35.063Z for commit 8a197f31.

## Unit tests (Vitest)

**127 passed, 0 failed, 0 skipped** of 127.


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
  "testedAt": "2026-10-14T09:59:14.968Z"
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
  "oneTimeModelLoadMs": 625,
  "timeToFirstTranslatedMessageMs": 197,
  "firstTranslatedAfterFirstRowMountMs": 103,
  "visibleMessages": 7,
  "newMessageLatencyMs": {
    "n": 10,
    "p50": 54,
    "p95": 68,
    "max": 68,
    "all": [
      53,
      53,
      68,
      53,
      54,
      54,
      55,
      54,
      54,
      53
    ]
  },
  "warmVisibleMessageLatencyMs": {
    "n": 7,
    "p50": 387,
    "p95": 437,
    "max": 437
  },
  "coldVisibleMessageLatencyMs": {
    "p50": 453,
    "p95": 502,
    "max": 502
  },
  "history": {
    "messagesTranslated": 435,
    "messagesPerSecond": 26.8,
    "engineTexts": 288,
    "seconds": 16.2,
    "engineTextsPerSecond": 17.7,
    "engineCalls": 149,
    "cacheHits": 0
  },
  "longTasks": {
    "count": 0,
    "totalMs": 0,
    "maxMs": 0
  },
  "idleMainThreadBusyMsPer3s": 7,
  "memory": {
    "pageJsHeapMB": 11,
    "extensionProcessRssMB_before": 1622,
    "extensionProcessRssMB_with3packs": 1632,
    "wasmHeapReservedMB": [
      "fr-en:523",
      "ja-en:853",
      "es-en:522"
    ],
    "allChromiumRssMB": 2649
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
