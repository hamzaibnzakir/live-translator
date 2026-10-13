# Brainbox Live Translator — real-screen self-test

Passed **4**, failed **0**, skipped **0**.

| Result | Check | Details |
|---|---|---|
| PASS | Real local LLM (Ollama) translates on-screen French | 'Bienvenue dans le monde de la traduction' → 'Welcome to the world of translation' after 11.7s (includes model load) |
| PASS | Real local LLM translates on-screen Japanese | '東京の天気は晴れです' → 'The weather in Tokyo is clear' |
| PASS | Real engine: unchanged text not re-translated | requests during 15 s idle: 0 |
| PASS | Real engine: new subtitle-style line translated with context | 'Le train arrive dans cinq minutes' → 'The train is arriving in five minutes.' in 4559 ms |

## Metrics
```json
{
  "engine": "ticks=59 interval=536ms capture=63.4ms detect=5.7ms ocr=447ms (max 1044, n=5) translate=8566ms (req=2, fail=0, lines=5) cache hit/miss=0/5 e2e=9497ms tracked=64 overlay=4",
  "model": "qwen2.5:1.5b"
}
```
