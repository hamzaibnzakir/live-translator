# Brainbox Translate — test report

Generated 2026-10-14T03:12:37.132Z for commit dc6e0d30.

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
  "testedAt": "2026-10-14T03:11:14.598Z"
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
  "oneTimeModelLoadMs": 989,
  "timeToFirstTranslatedMessageMs": 296,
  "firstTranslatedAfterFirstRowMountMs": 167,
  "visibleMessages": 6,
  "newMessageLatencyMs": {
    "n": 10,
    "p50": 56,
    "p95": 67,
    "max": 67,
    "all": [
      56,
      62,
      63,
      51,
      45,
      50,
      51,
      52,
      67,
      66
    ]
  },
  "warmVisibleMessageLatencyMs": {
    "n": 6,
    "p50": 445,
    "p95": 478,
    "max": 478
  },
  "coldVisibleMessageLatencyMs": {
    "p50": 523,
    "p95": 556,
    "max": 556
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
  "idleMainThreadBusyMsPer3s": 7,
  "memory": {
    "pageJsHeapMB": 8,
    "extensionProcessRssMB_before": 1613,
    "extensionProcessRssMB_with3packs": 1622,
    "wasmHeapReservedMB": [
      "fr-en:523",
      "ja-en:853",
      "es-en:522"
    ],
    "allChromiumRssMB": 2619
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
