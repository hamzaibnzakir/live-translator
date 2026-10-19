# Brainbox Translate — test report

Generated 2026-10-19T10:42:12.429Z for commit d60f916f.

## Unit tests (Vitest)

**132 passed, 0 failed, 0 skipped** of 132.


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
  "testedAt": "2026-10-19T10:40:36.892Z"
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
  "oneTimeModelLoadMs": 1054,
  "timeToFirstTranslatedMessageMs": 282,
  "firstTranslatedAfterFirstRowMountMs": 156,
  "visibleMessages": 7,
  "newMessageLatencyMs": {
    "n": 10,
    "p50": 65,
    "p95": 80,
    "max": 80,
    "all": [
      64,
      80,
      63,
      63,
      62,
      65,
      65,
      65,
      65,
      65
    ]
  },
  "warmVisibleMessageLatencyMs": {
    "n": 7,
    "p50": 534,
    "p95": 601,
    "max": 601
  },
  "coldVisibleMessageLatencyMs": {
    "p50": 569,
    "p95": 635,
    "max": 635
  },
  "history": {
    "messagesTranslated": 435,
    "messagesPerSecond": 25.9,
    "engineTexts": 288,
    "seconds": 16.8,
    "engineTextsPerSecond": 17.1,
    "engineCalls": 134,
    "cacheHits": 0
  },
  "longTasks": {
    "count": 0,
    "totalMs": 0,
    "maxMs": 0
  },
  "idleMainThreadBusyMsPer3s": 8,
  "memory": {
    "pageJsHeapMB": 7,
    "extensionProcessRssMB_before": 1614,
    "extensionProcessRssMB_with3packs": 1624,
    "wasmHeapReservedMB": [
      "fr-en:523",
      "ja-en:853",
      "es-en:522"
    ],
    "allChromiumRssMB": 2640
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
