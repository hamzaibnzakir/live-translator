# Brainbox Live Translator — real-screen self-test

Passed **26**, failed **0**, skipped **1**.

| Result | Check | Details |
|---|---|---|
| INFO | Probe: overlay surface Layered | Layered: affinity accepted=False (err 8), hidden from capture=False, draws=True, transparent background=True |
| INFO | Probe: overlay surface Redirected | Redirected: affinity accepted=True (err 0), hidden from capture=True, draws=True, transparent background=True |
| INFO | Probe: Desktop Duplication | Desktop Duplication vs GDI: 100% identical pixels, 0.1% black |
| PASS | System tray icon created | Tray menu with Live/Target/Engine/Mode/Glow/Pause/Settings/Restart/Exit |
| PASS | T1 Foreign text detected & translated automatically | 'Bienvenue dans le monde de la traduction' → 'Welcome to the world of translation' in 1971 ms with no user action |
| PASS | T1b Translation placed over the original text | source [152,210 483x35], overlay [151,217 486x23] |
| PASS | T1c Overlay actually rendered on screen | 3 item(s) on overlay windows |
| PASS | T1d Japanese detected & translated | '東京の天気は晴れです' → 'The weather in Tokyo is sunny' |
| PASS | T2 English text is ignored (not English→English) | sent=False, overlay=False |
| PASS | T11a Translation overlay excluded from screen capture | SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) accepted for all layers |
| PASS | T11b Capture under the overlay shows the original, not the translation | OCR of captured area: 'ft / Bienvenue dans le monde de la traduction' |
| PASS | T11c Translator never translates its own overlay | segments sent so far: Bienvenue dans le monde de la traduction / 東京の天気は晴れです / ー[ 172ヨ9fヨ- f9eß - 8 [十 |
| PASS | T7 Unchanged on-screen text is not re-translated | translation requests during 15 s idle: 0; OCR passes: 0 |
| PASS | T10 Overlay does not block mouse input | click at (393,227) under overlay=True reached the app: True; translation peeked away under pointer: True; overlay took focus: False |
| PASS | T6 Overlay follows moved window | overlay [151,217 486x23] → [451,357 486x23], text now at [452,350 483x35] |
| PASS | T6b Moved text served from cache (no new request) | times the moved French line was re-sent for translation: 0 |
| PASS | Text change updates the translation | now 'The train arrives in five minutes' |
| PASS | Overlay disappears when the source text disappears | removed |
| PASS | T8 Local AI closed: app keeps running and reports it | running=True, state=Degraded, message='Custom AI is not reachable at http://127.0.0.1:59912/v1 (No connection could be made because the target machine actively refused it. (127.0.0.1:59912)).' |
| PASS | T8b Recovers automatically when the local AI is back | state=Live |
| PASS | Survives maximized/fullscreen window changes | overlay present after maximize: True |
| PASS | Hotkey Ctrl+Alt+P pauses and resumes | paused=True, overlay hidden while paused=True, resumed=True |
| PASS | Hotkey Ctrl+Alt+G toggles the glow | glow True → False |
| PASS | Hotkey Ctrl+Alt+B toggles live translation | turned off=True, overlay cleared=True |
| PASS | Glow indicator visible on every monitor edge | 4 edge strips, visible=True, excluded from capture=False (masked from detection otherwise) |
| PASS | Start-with-Windows registration | HKCU Run entry created with --autostart and removed again (reboot not performed in this test) |
| PASS | Virtual desktop switching keeps translation active | switch detected=True, new desktop translated=True, overlay followed=True, back on desktop 1 overlay ok=True, translation after return=True |
| SKIP | Multiple monitors | only one display attached here (multi-monitor coordinate logic covered by unit tests) |
| PASS | Desktop Duplication (GPU) capture backend | backend=Desktop Duplication (GPU) |
| PASS | Application stayed alive through all scenarios | watcher loop running |

## Metrics
```json
{
  "os": "Microsoft Windows NT 10.0.20348.0",
  "ocrLanguages": [
    "en-US",
    "ja"
  ],
  "monitors": [
    {
      "DeviceName": "\\\\.\\DISPLAY1",
      "Bounds": "[0,0 1920x1080]",
      "DpiScale": 1,
      "IsPrimary": true
    }
  ],
  "idle": {
    "cpuPercentOfMachine": 3.44,
    "workingSetMB": 303.1,
    "privateMB": 198.9,
    "gpuPercent": 1.41,
    "ticksPerSecond": 3.63,
    "avgDetectionIntervalMs": 247.6,
    "avgOcrMs": 1239.7,
    "avgTranslationMs": 110
  },
  "active": {
    "cpuPercentOfMachine": 5.93,
    "workingSetMB": 418.5,
    "privateMB": 308.5,
    "gpuPercent": 1.5,
    "ticksPerSecond": 3.8,
    "avgDetectionIntervalMs": 250.1,
    "avgOcrMs": 169.7,
    "avgTranslationMs": 36
  },
  "engine": {
    "AvgTickIntervalMs": 250.05958097197188,
    "AvgCaptureMs": 21.56618445135772,
    "AvgDetectMs": 1.5190876768078028,
    "AvgOcrMs": 169.7246632194916,
    "MaxOcrMs": 1239.7234,
    "OcrCalls": 21,
    "AvgTranslationMs": 36.0448,
    "TranslationRequests": 8,
    "CacheHits": 20,
    "CacheMisses": 8,
    "AvgEndToEndMs": 24.226837255587704
  }
}
```
