# Brainbox Translate — test report

Generated 2026-10-13T16:43:18.924Z for commit 81285e74.

## Unit tests (Vitest)

**103 passed, 0 failed, 0 skipped** of 103.


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
  "testedAt": "2026-10-13T16:42:08.210Z"
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
  "oneTimeModelLoadMs": 1080,
  "timeToFirstTranslatedMessageMs": 298,
  "firstTranslatedAfterFirstRowMountMs": 165,
  "visibleMessages": 6,
  "newMessageLatencyMs": {
    "n": 10,
    "p50": 65,
    "p95": 76,
    "max": 76,
    "all": [
      73,
      65,
      65,
      51,
      68,
      76,
      49,
      49,
      63,
      65
    ]
  },
  "warmVisibleMessageLatencyMs": {
    "n": 6,
    "p50": 443,
    "p95": 476,
    "max": 476
  },
  "coldVisibleMessageLatencyMs": {
    "p50": 511,
    "p95": 544,
    "max": 544
  },
  "history": {
    "messagesTranslated": 435,
    "messagesPerSecond": 26.4,
    "engineTexts": 287,
    "seconds": 16.5,
    "engineTextsPerSecond": 17.4,
    "engineCalls": 148,
    "cacheHits": 0
  },
  "longTasks": {
    "count": 0,
    "totalMs": 0,
    "maxMs": 0
  },
  "idleMainThreadBusyMsPer3s": 9,
  "memory": {
    "pageJsHeapMB": 12,
    "extensionProcessRssMB_before": 1625,
    "extensionProcessRssMB_with3packs": 1636,
    "wasmHeapReservedMB": [
      "fr-en:523",
      "ja-en:853",
      "es-en:522"
    ],
    "allChromiumRssMB": 2638
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
