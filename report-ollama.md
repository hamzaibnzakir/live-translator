# Brainbox Live Translator — real-screen self-test

Passed **4**, failed **0**, skipped **0**.

| Result | Check | Details |
|---|---|---|
| PASS | Real local LLM (Ollama) translates on-screen French | 'Bienvenue dans le monde de la traduction' → 'Welcome to the world of translation' after 74.2s (includes model load) |
| PASS | Real local LLM translates on-screen Japanese | '東京の天気は晴れです' → 'The weather in Tokyo is clear.' |
| PASS | Real engine: unchanged text not re-translated | requests during 15 s idle: 0 |
| PASS | Real engine: new subtitle-style line translated with context | 'Le train arrive dans cinq minutes' → 'The train is arriving in five minutes.' in 4115 ms |

## Metrics
```json
{
  "engine": "ticks=182 interval=537ms capture=52.3ms detect=12.8ms ocr=373ms (max 1043, n=6) translate=35978ms (req=3, fail=1, lines=5) cache hit/miss=0/5 e2e=59366ms tracked=56 overlay=4",
  "model": "qwen2.5:1.5b"
}
```
