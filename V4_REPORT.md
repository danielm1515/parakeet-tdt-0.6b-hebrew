# Hebrew Parakeet-TDT-0.6B — v4 Report

Fine-tuning `nvidia/parakeet-tdt-0.6b-v3` for Hebrew. This report covers the v4 iteration:
external-LM decoding, a 2.8× larger consensus-cleaned training corpus, and evaluation across
multiple real-world Hebrew benchmarks (podcasts, WhatsApp voice notes, clean read speech).

**TL;DR:** v4 improves on v3 across every benchmark — and the gains are *far* larger on
realistic held-out audio than on the in-domain podcast set v3 was already tuned for. On
clean directed speech (ivrit-ai `eval-d1`), **v4 reaches 7.9% WER** (v3: 14.2%).

---

## 1. Headline: v3 vs v4 on the same benchmarks (greedy decoding)

| Benchmark | Content | v3 WER | **v4 WER** | Δ |
|---|---|---|---|---|
| **ivrit-ai eval-d1** (17 clips) | clean, directed | 14.19% | **7.90%** | **−6.29** |
| **ivrit-ai eval-whatsapp** (54 clips) | casual phone voice notes | 13.82% | **11.37%** | **−2.45** |
| FLEURS he (792 clips) | encyclopedic (hard vocab) | 28.22% | **24.71%** | −3.51 |
| crowd-transcribe test (20,117 clips) | conversational podcasts, noisy | 13.25% | **12.13%** | −1.12 |

**Key insight:** WER depends overwhelmingly on the *content/vocabulary* of the audio, not
just recording quality — the same model spans 7.9%→24.7% across sets. v4's extra data
generalizes much better than v3, but the improvement is masked on the crowd-transcribe test
because v3 was already optimized on that exact domain.

### Comparison vs `ivrit-ai/whisper-large-v3-turbo` (greedy, identical normalization)

| Benchmark | v3 | v4 (ours) | **ivrit-ai Whisper turbo** |
|---|---|---|---|
| eval-d1 | 14.19% | 7.90% | **4.80%** |
| eval-whatsapp | 13.82% | 11.37% | **7.68%** |
| FLEURS he | 28.22% | 24.71% | **18.88%** |

**Honest read:** ivrit-ai's Whisper turbo is more accurate than v4 (~3–6% WER lower). That is
expected — it is a larger model (809M vs 617M), it was *pretrained on Hebrew* (Whisper large-v3
saw ~5M h multilingual incl. Hebrew) whereas parakeet-tdt-0.6b was pretrained on European
languages with **no Hebrew**, and these are ivrit-ai's own eval sets. **However**, parakeet v4
is smaller, several× faster, and — critically for a voice agent — supports **low-latency
streaming / real-time** decoding (TDT transducer), which Whisper large-v3-turbo does not.

**Path to close the accuracy gap (recommended): knowledge distillation.** Re-label the 8,391 h
corpus with ivrit-ai Whisper turbo (a far better teacher than the original Whisper pseudo-labels),
then retrain parakeet on those labels — distilling Whisper-grade accuracy into a fast streaming
model. Combined with a larger base (parakeet-tdt-1.1b), this is the credible route to
Whisper-class accuracy at a fraction of the latency.

---

## 2. Decoding stack on the crowd-transcribe test (20k clips)

| Decoding | v3 | **v4** |
|---|---|---|
| greedy | 13.25% | 12.13% |
| beam search (malsd_batch, beam=8) | 12.72% | 11.68% |
| **beam + external-text KenLM (α=0.15)** | **12.00%** | **11.22%** |
| CER (best) | 6.03% | **5.48%** |

Cumulative on this benchmark: **13.25% → 11.22%** (−2.03 absolute).

---

## 3. What changed from v3 → v4

**Phase 1 — Decoding (no training).** Switched greedy → **beam search** (NeMo `malsd_batch`,
beam=8) and added **shallow fusion with an external-text 5-gram KenLM** trained on 44.5M
normalized sentences from Hebrew Wikipedia + HeDC4 web text. Best α = 0.15. (Note: the
formal-text LM can *hurt* on very casual/colloquial speech — tune per target domain.)

**Phase 2 — Data scale + consensus cleaning.** Grew the corpus **3,000 h → 8,391 h**:

| Source | Hours | Clips |
|---|---|---|
| ivrit-ai/audio-v2 (consensus-filtered) | 7,594 | 12.7M |
| ivrit-ai/knesset-plenums | 511 | 566k |
| ivrit-ai/crowd-transcribe-v5 | 254 | 196k |
| ivrit-ai/crowd-recital | 32 | 43k |
| **Total** | **8,391** | **13.5M** |

Cleaning = **consensus filtering**: transcribe every clip with v3, compare to the Whisper
pseudo-label (CER), keep clips with CER ≤ 0.20 (dropped the worst ~15% — bad audio or bad
labels), keeping 7,594 h of 9,010 h raw audio-v2.

**Phase 3 — Staged fine-tune (v4).** Continued from v3 through the proven staged recipe
(stage-2 48k steps + stage-3 15k polish = 63k steps ≈ 8M samples, same *training budget* as
v3 but drawn from the 2.8× larger, cleaner pool). 8× RTX 5090, bf16 DDP.

---

## 4. Practical takeaways

- **For a general voice agent (clean mic, directed speech):** v4 is already **~8% WER** on
  matched audio (eval-d1) — below the 10% target — before beam/LM. The 11–12% podcast number
  is the *hardest* benchmark, not the representative one.
- **The single biggest remaining lever is domain-matched data:** a few hundred hours of the
  actual target audio, fine-tuned from v4, is the reliable path to the lowest WER on that
  specific use case.
- **Lower-ROI (documented, not done):** tokenizer rebuild (2048) ≈ full retrain for ~0.5%;
  more epochs (still-declining val_wer) ~0.5–1.5% but 12–16 h/epoch and risk of over-fitting
  noisy labels.

---

*Model: `parakeet-tdt-0.6b-hebrew-v4.nemo`. Base: `nvidia/parakeet-tdt-0.6b-v3` with a Hebrew
BPE-1024 tokenizer. Eval: NeMo greedy_batch / malsd_batch, transcripts normalized with the
training normalizer (nikud stripped, digits→words). Long-form clips (whatsapp/d1) evaluated at
batch=1 greedy; beam OOMs on multi-minute audio and is unnecessary for short agent utterances.*
