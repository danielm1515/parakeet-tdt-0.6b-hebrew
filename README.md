# 🎙️ Parakeet‑TDT 0.6B — Hebrew

**A fast, real‑time Hebrew speech‑to‑text model** — a Hebrew fine‑tune of NVIDIA's
`parakeet‑tdt‑0.6b` (FastConformer + TDT transducer), built for low‑latency,
streaming voice applications.

<p align="center">
  <img alt="WER" src="https://img.shields.io/badge/WER-14.0%25-2ea44f">
  <img alt="CER" src="https://img.shields.io/badge/CER-6.6%25-2ea44f">
  <img alt="Speed" src="https://img.shields.io/badge/Speed-~714%C3%97_realtime-blue">
  <img alt="Latency" src="https://img.shields.io/badge/Latency-~62ms-blue">
  <img alt="Language" src="https://img.shields.io/badge/Language-Hebrew-orange">
</p>

---

## ⭐ Headline results

Evaluated on **real, spontaneous Hebrew** — conversational **WhatsApp voice messages**
(the hardest setting for ASR: noisy, accents, disfluencies, no read‑speech comfort).

| Metric | Result |
|---|---|
| **Word Error Rate (WER)** | **14.0 %** |
| **Character Error Rate (CER)** | **6.6 %** |
| **Throughput** | **≈ 714× real‑time** (RTF 0.0014) |
| **Latency** (short utterance) | **≈ 62 ms** |
| One hour of audio transcribed in | **≈ 5 seconds** |

> 14 % WER on **spontaneous conversational Hebrew** is a strong result — this is the
> domain where most ASR systems struggle the most.

---

## 🧭 The journey — measured, not guessed

The model was improved **iteratively**, and every version was evaluated on the same
held‑out benchmark. Progress was tracked with hard numbers, not vibes:

| Version | WER | CER |
|:--:|:--:|:--:|
| v1 | 23.5 % | 10.7 % |
| v2 | 21.6 % | 9.5 % |
| v2.1 | 25.9 % | 17.3 % |
| **v3 ✅ (current)** | **14.0 %** | **6.6 %** |

Two takeaways this process surfaced:
- **v2.1 was a regression** — caught immediately by evaluation, not shipped blindly.
- **v3 nearly halved the error rate** vs the first version (23.5 % → 14.0 %).

*Rigorous, dataset‑driven iteration is what turned a decent model into a strong one.*

---

## ⚡ Speed — the differentiator

Parakeet‑TDT is designed for **real‑time**. With decoding optimizations
(CUDA‑graph transducer decoding + bfloat16 compute), throughput scales dramatically
**with no loss in accuracy**:

| Configuration | RTF | × real‑time |
|---|:--:|:--:|
| baseline | 0.0047 | ~213× |
| + CUDA‑graph decoding | 0.0032 | ~313× |
| **+ bfloat16** | **0.0014** | **~714×** |

- **~714× faster than real‑time** in batch/throughput mode.
- **~62 ms** to transcribe a typical spoken sentence — comfortable for live voice agents.
- Runs on a single consumer GPU.

---

## ⚖️ Accuracy vs. Speed — an honest comparison

Benchmarked head‑to‑head against a strong reference on the **exact same audio**:

| Model | WER | CER | Speed |
|---|:--:|:--:|:--:|
| Whisper‑large‑v3‑turbo (Hebrew) | 7.0 % | 3.3 % | ~43× real‑time |
| **Parakeet‑TDT 0.6B Hebrew (this)** | 14.0 % | 6.6 % | **~714× real‑time** |

The trade‑off is clear and deliberate:

- **Whisper** is more accurate — the right choice for offline, accuracy‑first transcription.
- **Parakeet** is **~16× faster** and streaming‑native — the right choice for **real‑time voice
  agents, live captioning, on‑device, and high‑throughput** pipelines, where every millisecond counts.

Same accuracy tier isn't the goal here — **usable accuracy at real‑time speed** is.

---

## 🎯 Where it shines

- 🗣️ **Real‑time voice agents** — sub‑100 ms transcription keeps conversations natural.
- 📺 **Live captioning** — transcribe as people speak.
- 📱 **On‑device / edge** — small (0.6B) and fast.
- 📦 **Bulk transcription** — an hour of audio in seconds.

---

## 🔬 Evaluation methodology (for the sceptics)

- **Benchmark:** real Hebrew WhatsApp voice messages — spontaneous, in‑the‑wild speech.
- **Metrics:** WER + CER, computed with standard tooling.
- **Fair normalization:** niqqud (vowel points) and punctuation removed on both reference
  and hypothesis, so scoring reflects *words*, not diacritic/punctuation choices.
- **Long audio** is windowed and re‑joined, then scored against the full reference.
- Whisper comparison run on the **identical clips** with the **identical normalization**.

---

## 📌 Summary

A Hebrew ASR model that is **fast, streaming‑ready, and accurate enough for real‑time** —
**14 % WER on hard conversational speech at ~714× real‑time.** Improved through disciplined,
metric‑driven iteration.

<sub>Base architecture: NVIDIA parakeet‑tdt‑0.6b (FastConformer‑TDT). Numbers measured on real
conversational Hebrew audio; your mileage will vary by domain and audio quality.</sub>
