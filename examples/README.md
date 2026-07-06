# Examples — running the model for real

Two small, self-contained pieces that turn `parakeet-tdt-0.6b-hebrew` into a working
local transcription stack. **Everything runs on your machine — audio never leaves it.**

```
┌──────────────┐   PCM16 @16k    ┌──────────────────────────┐
│  MilaWin      │ ──────────────► │  stt_parakeet_server.py  │
│  (tray app)   │ ◄────────────── │  parakeet-tdt-0.6b-hebrew │
└──────────────┘   Hebrew text   └──────────────────────────┘
```

## 1. `stt_parakeet_server.py` — the model as a local HTTP service

A minimal FastAPI wrapper around the model: `POST /stt` with raw PCM16 mono 16 kHz
bytes → `{"text": "..."}`. Loads the `.nemo` once with CUDA-graph greedy TDT decoding
and bfloat16 (the ~768× real-time configuration).

```bash
pip install nemo_toolkit fastapi uvicorn soundfile
set SPEECH_STT_NEMO=path\to\parakeet-tdt-0.6b-hebrew-v4.nemo
python stt_parakeet_server.py        # -> http://127.0.0.1:7877/stt
```

## 2. MilaWin — live Hebrew dictation for Windows

A system-tray dictation app (inspired by [mila](https://github.com/island-io/mila) for
macOS) built entirely on this model. Press the hotkey and the words you speak are typed
at your cursor — in any application.

- **Live streaming dictation** — text appears *while you speak* (the model is fast
  enough to re-transcribe the whole utterance every 700 ms), settling to the final
  transcription on every pause (energy VAD).
- **Three modes** — live stream / sentence-by-sentence / single batch pass for maximum
  accuracy.
- **System-audio transcription** — capture what the PC *plays* (WASAPI loopback):
  meetings, videos, podcasts.
- **Mic picker**, persisted settings, single-instance, hotkey fallback chain.

```bash
cd MilaWin
dotnet run -c Release      # needs .NET 8+; the STT server above must be running
```

Why this model makes the app feel instant: a 5–6 s utterance transcribes in
~80–150 ms, so live partials cost nothing and the final text lands the moment you
stop talking.
