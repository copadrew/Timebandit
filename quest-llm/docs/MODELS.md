# Choosing a model

## The memory ceiling is the whole design constraint

Quest 3 has 8 GB of physical RAM. Horizon OS, the compositor and the reserved
display buffers take a large share of it, and what an app can actually touch is
in the neighbourhood of 5–6 GB. Out of that you still have to pay for:

| Consumer | Typical cost |
|---|---|
| Unity runtime, IL2CPP, managed heap | 300–600 MB |
| Eye render targets + compositor layers | 200–400 MB |
| GGUF weights (mmap'd, but resident once touched) | *the number below* |
| KV cache | see the table further down |

Which leaves roughly **3.5 GB for weights if you want any headroom at all**, and
you do want headroom — an OOM on Quest is not a caught exception, it is the app
disappearing back to the home environment.

## So: 7B is out

This is the part of the original spec that does not survive contact with the
device. A 7B model at Q4_K_M is about 4.1 GB of weights before the KV cache.
An 8B is closer to 4.9 GB. Either one fits in a spreadsheet and not in a headset
that is also rendering stereo at 72 Hz.

Approximate Q4_K_M sizes, weights only:

| Model | Weights | Verdict |
|---|---|---|
| Llama 3.2 1B | ~0.8 GB | Comfortable. Fast. Noticeably dim. |
| Qwen2.5 3B | ~1.9 GB | **Best default.** |
| Llama 3.2 3B | ~2.0 GB | **Best default.** |
| Phi-3.5-mini (3.8B) | ~2.2 GB | Fine, stronger at reasoning, chattier. |
| Mistral 7B | ~4.1 GB | Will not leave room to render. |
| Llama 3.1 8B | ~4.9 GB | No. |

## The KV cache is not free either

People size the model and forget this. Per token, the cache costs
`2 × n_kv_heads × head_dim × n_layers × 2 bytes` at f16.

| Model | Per token | At `n_ctx` 2048 | At `n_ctx` 4096 |
|---|---|---|---|
| Llama 3.2 1B (16 layers, 8 KV heads, d64) | ~32 KB | ~67 MB | ~134 MB |
| Llama 3.2 3B (28 layers, 8 KV heads, d128) | ~114 KB | ~235 MB | ~470 MB |

That is why `contextTokens` defaults to 2048 rather than the 8192 the model card
advertises. Doubling the window costs a quarter of a gigabyte and buys you
conversation history nobody in a headset is going to scroll back through.

## Expected speed

On the XR2 Gen 2, pinned to three mid cores, with the renderer running:

- **1B Q4_K_M:** roughly 15–25 tok/s
- **3B Q4_K_M:** roughly 5–10 tok/s
- Prompt evaluation is several times faster per token than generation, but a
  long system prompt still costs a visible pause on the first turn.

Those are starting expectations, not promises — measure with the `tok/s` readout
in the status line. Expect all of them to sag as the headset warms up; that is
`ThermalGovernor` doing its job rather than a bug.

## Uncensored / abliterated models

These are ordinary open-weight models with the refusal behaviour tuned or
ablated out, and they are on Hugging Face in GGUF form like anything else. Real
options, all small enough for this device:

- `huihui-ai/Llama-3.2-3B-Instruct-abliterated` — GGUF quants are republished by
  `mradermacher` and `bartowski`.
- `huihui-ai/Qwen2.5-3B-Instruct-abliterated` — same story; Qwen2.5 3B is the
  stronger base of the two in my experience.
- `cognitivecomputations/Dolphin3.0-Llama3.2-3B` — Dolphin is *trained* uncensored
  rather than ablated after the fact, which tends to hold together better.

Exact repository names on Hugging Face drift as people re-upload quants, so
search for the base name and pick a `Q4_K_M` file rather than trusting a path
here to still resolve.

**Two practical warnings, offered as engineering rather than as a lecture:**

1. **Abliteration costs capability.** Removing the refusal direction from the
   residual stream is a blunt instrument, and it measurably degrades
   instruction-following and multi-step reasoning. On a 3B model there is not
   much headroom to give away — an abliterated 3B is meaningfully worse at
   ordinary tasks than the model it came from, not just less prone to refusing.
   Dolphin-style fine-tunes generally hold up better than post-hoc ablation.

2. **Try a system prompt first.** A large share of what people want an
   abliterated model for is really just "stop moralising at me and answer in the
   register I asked for", and that is a system-prompt problem. `systemPrompt` on
   `LlamaRunner` is right there, it costs no capability, and the model stays as
   smart as it shipped. Worth trying before you take the quality hit.

Whatever you pick, it runs entirely on the headset — nothing is transmitted, and
the usual caveat applies that a small model's factual output should not be
trusted for anything that matters regardless of how it was tuned.

## Verify the chat template before you blame the code

The single most common "the model outputs garbage" cause is a GGUF with no
embedded chat template. The wrapper reads it with `llama_model_chat_template()`
and falls back to a llama.cpp default, which will not match a Qwen or Phi model.
Check before you debug anything else:

```bash
# from a llama.cpp checkout
python3 gguf-py/gguf/scripts/gguf_dump.py --no-tensors model.gguf | grep -i chat_template
```

No result means you need a different quant of the same model, or an explicit
template.

## Whisper model for voice input

`ggml-base.en-q5_1.bin` (~60 MB) is the right size here. `small` is roughly four
times the compute for an accuracy gain you will not notice on short prompts
spoken into a headset mic, and it competes with the LLM for cores while doing
it. Grab it from `ggerganov/whisper.cpp` on Hugging Face.
