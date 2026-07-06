# -*- coding: utf-8 -*-
"""Parakeet STT sidecar for non-Python voice-agent servers (e.g. the C# port).

Same model + loading recipe as server.py's in-process ParakeetASR (keep in sync).

    POST /stt   raw PCM16 mono @16k body -> {"text": "...", "ms": 123}
    GET  /healthz

Start (venv_omnivoice):
    C:/developer/newtts/venv_omnivoice/Scripts/python.exe stt_parakeet_server.py
"""
import contextlib
import os
import sys
import tempfile
import time

import numpy as np
import soundfile as sf
import torch
from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8")

MODEL_PATH = os.environ.get(
    "SPEECH_STT_NEMO",
    r"G:\SpeechEngine\models\parakeet\parakeet-tdt-0.6b-hebrew-v4.nemo")
SR = 16000


class ParakeetASR:
    """NeMo parakeet-tdt-0.6b-hebrew — greedy_batch CUDA-graph decoding,
    bf16 autocast, temp-WAV transcribe (mirror of server.py)."""

    def __init__(self, path: str, device: str = "cuda"):
        import logging as _pylog
        from nemo.collections.asr.models import ASRModel
        try:
            from nemo.utils import logging as _nemo_logging
            _nemo_logging.setLevel(_pylog.ERROR)
        except Exception:
            pass
        _pylog.getLogger("nemo_logger").setLevel(_pylog.ERROR)
        if device == "cuda" and not torch.cuda.is_available():
            print("  [warn] CUDA not available -> CPU")
            device = "cpu"
        model = ASRModel.restore_from(path, map_location=device)
        if device == "cuda":
            model = model.cuda()
        model.eval()
        if device == "cuda":
            try:
                from omegaconf import open_dict
                dcfg = model.cfg.decoding
                with open_dict(dcfg):
                    dcfg.strategy = "greedy_batch"
                    if dcfg.get("greedy") is None:
                        dcfg.greedy = {}
                    dcfg.greedy.use_cuda_graph_decoder = True
                model.change_decoding_strategy(dcfg)
            except Exception:
                pass
        try:
            from nemo.utils import logging as _nl
            _nl.setLevel(_pylog.ERROR)
        except Exception:
            pass
        _pylog.getLogger("nemo_logger").setLevel(_pylog.ERROR)
        self.model = model
        self.device = device
        self._tmp = tempfile.mkdtemp(prefix="parakeet_stt_")
        self._tmp_i = 0

    @contextlib.contextmanager
    def _hush_stderr(self):
        try:
            fn = os.open(os.devnull, os.O_WRONLY); old = os.dup(2); os.dup2(fn, 2)
        except Exception:
            yield; return
        try:
            yield
        finally:
            try:
                os.dup2(old, 2); os.close(fn); os.close(old)
            except Exception:
                pass

    def transcribe_text(self, audio: np.ndarray) -> str:
        if audio.size < int(0.2 * SR):
            return ""
        p = os.path.join(self._tmp, f"u_{self._tmp_i % 4}.wav"); self._tmp_i += 1
        sf.write(p, np.clip(audio, -1, 1), SR, subtype="PCM_16")
        amp = (torch.autocast("cuda", dtype=torch.bfloat16)
               if self.device == "cuda" else contextlib.nullcontext())
        try:
            with self._hush_stderr(), amp:
                out = self.model.transcribe([p], batch_size=1, num_workers=0, verbose=False)
        except TypeError:
            with self._hush_stderr(), amp:
                out = self.model.transcribe([p], batch_size=1)
        h = out[0] if out else ""
        return (getattr(h, "text", None) or (h if isinstance(h, str) else "")).strip()


print(f"[stt] loading {os.path.basename(MODEL_PATH)} ...")
asr = ParakeetASR(MODEL_PATH)
asr.transcribe_text(np.zeros(SR, dtype=np.float32))  # capture CUDA graph
print("[stt] ready")

app = FastAPI()


@app.get("/healthz")
def healthz():
    return {"ok": True, "model": os.path.basename(MODEL_PATH), "sr": SR}


@app.post("/stt")
async def stt(request: Request):
    body = await request.body()
    audio = np.frombuffer(body, dtype=np.int16).astype(np.float32) / 32768.0
    t0 = time.perf_counter()
    text = asr.transcribe_text(audio)
    ms = int((time.perf_counter() - t0) * 1000)
    return JSONResponse({"text": text, "ms": ms})


if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="127.0.0.1", port=7877, log_level="error")
