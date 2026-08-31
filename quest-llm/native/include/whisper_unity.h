/*
 * whisper_unity.h — minimal C ABI over whisper.cpp for on-device speech input.
 *
 * Why this exists at all: Meta's Voice SDK (Wit.ai) is a cloud service. If the
 * point of the project is that nothing leaves the headset, dictation has to run
 * locally too, which means a second model and a second consumer of the same
 * three CPU cores the LLM is already using. Transcription and generation must
 * therefore be serialised — see WHISPER_CONCURRENCY in the README.
 *
 * Same asynchronous, poll-based shape as llama_unity.h and the same threading
 * rule: drive one context from one thread.
 */
#ifndef WHISPER_UNITY_H
#define WHISPER_UNITY_H

#include <stdint.h>

#if defined(_WIN32)
#  define WU_API __declspec(dllexport)
#else
#  define WU_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct wu_context wu_context;

enum wu_status {
    WU_IDLE    = 0,
    WU_RUNNING = 1,
    WU_DONE    = 2,
    WU_ERROR   = 3
};

enum wu_result {
    WU_OK              =  0,
    WU_ERR_GENERIC     = -1,
    WU_ERR_BAD_ARG     = -2,
    WU_ERR_LOAD_FAILED = -3,
    WU_ERR_BUSY        = -4
};

/* Audio handed to wu_submit must be 16 kHz, mono, float32 in [-1, 1].
 * Unity's Microphone rarely gives you that directly; VoiceInput.cs resamples. */
#define WU_SAMPLE_RATE 16000

typedef struct {
    int32_t  n_threads;
    uint32_t cpu_mask;      /* same meaning as lu_params.cpu_mask; 0 = unpinned */
    int32_t  translate;     /* 1 = translate to English instead of transcribing  */
    char     language[8];   /* ISO code, or "auto"                               */
} wu_params;

WU_API void wu_defaults(wu_params * p);

/* Loads a ggml whisper model (e.g. ggml-base.en-q5_1.bin). NULL on failure. */
WU_API wu_context * wu_load(const char * model_path, const wu_params * params);
WU_API void wu_free(wu_context * ctx);

/* Queues one utterance for transcription. `samples` is copied, so the caller
 * may reuse its buffer immediately. Returns WU_OK or a negative wu_result. */
WU_API int32_t wu_submit(wu_context * ctx, const float * samples, int32_t n_samples);

/* Non-blocking. On WU_DONE, copies the transcript into buf (NUL-terminated)
 * and returns WU_DONE exactly once; the transcript is cleared afterwards. */
WU_API int32_t wu_poll(wu_context * ctx, char * buf, int32_t buf_cap, int32_t * out_written);

WU_API int32_t wu_status_of(const wu_context * ctx);
WU_API const char * wu_last_error(const wu_context * ctx);

#ifdef __cplusplus
}
#endif
#endif /* WHISPER_UNITY_H */
