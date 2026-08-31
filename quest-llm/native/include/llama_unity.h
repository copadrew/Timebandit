/*
 * llama_unity.h — minimal C ABI over llama.cpp, shaped for P/Invoke from Unity.
 *
 * Design notes that matter for the caller:
 *
 *  - Generation is asynchronous. lu_submit() hands a prompt to a worker thread
 *    owned by this library and returns immediately. The caller drains output
 *    with lu_poll() from whatever thread it likes (in Unity: Update()).
 *
 *  - There are deliberately NO function-pointer callbacks into managed code.
 *    A native thread calling back into IL2CPP requires the thread to be
 *    attached, the target to be a static [MonoPInvokeCallback], and it still
 *    may not touch any Unity API. Polling a byte queue removes that entire
 *    class of crash for the cost of one call per frame.
 *
 *  - lu_poll() never splits a UTF-8 sequence across two calls, so the managed
 *    side can decode each chunk independently.
 *
 * A context is single-producer/single-consumer: call lu_submit/lu_poll/lu_cancel
 * from one thread only (the Unity main thread). All of them are cheap and
 * non-blocking.
 */
#ifndef LLAMA_UNITY_H
#define LLAMA_UNITY_H

#include <stdint.h>

#if defined(_WIN32)
#  define LU_API __declspec(dllexport)
#else
#  define LU_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct lu_context lu_context;

/* Return / status codes. */
enum lu_status {
    LU_IDLE      = 0,  /* loaded, no job in flight                     */
    LU_RUNNING   = 1,  /* worker is decoding                           */
    LU_DONE      = 2,  /* last job finished normally (EOG or max_tokens)*/
    LU_ERROR     = 3,  /* last job failed; see lu_last_error()          */
    LU_CANCELLED = 4   /* last job was stopped by lu_cancel()           */
};

enum lu_result {
    LU_OK              =  0,
    LU_ERR_GENERIC     = -1,
    LU_ERR_BAD_ARG     = -2,
    LU_ERR_LOAD_FAILED = -3,
    LU_ERR_BUSY        = -4,  /* submit while a job is already running  */
    LU_ERR_CONTEXT_FULL= -5   /* prompt cannot fit in n_ctx even empty  */
};

typedef struct {
    int32_t  n_ctx;              /* KV window in tokens. 2048 is plenty in VR. */
    int32_t  n_threads;          /* keep <= number of cores in cpu_mask        */
    int32_t  n_batch;            /* prompt-eval chunk size                     */
    int32_t  n_gpu_layers;       /* 0. See README: the GPU is the renderer's.  */
    uint32_t seed;               /* 0xFFFFFFFF for random                      */

    float    temperature;
    float    top_p;
    int32_t  top_k;
    float    repeat_penalty;
    int32_t  repeat_last_n;

    /* Affinity mask for the worker thread, bit N == CPU N. 0 = do not pin.
     * On Quest 3 (XR2 Gen 2) the render thread wants the prime core; pinning
     * generation to a subset of the mid cores keeps it off the renderer's back.
     * 0x70 (cores 4,5,6) is a sane starting point — measure, do not trust it. */
    uint32_t cpu_mask;

    /* Sleep inserted after every generated token. The thermal governor on the
     * managed side raises this as the headset heats, trading tokens/sec for
     * sustained runtime instead of letting the SoC throttle everything
     * including the compositor. 0 = full speed. */
    int32_t  inter_token_delay_us;

    /* Hard ceiling on how long one job may run, regardless of max_tokens.
     * 0 = no limit. */
    int32_t  max_job_ms;
} lu_params;

/* Fills p with defaults tuned for Quest 3. Always call this before overriding. */
LU_API void lu_defaults(lu_params* p);

/* Process-wide. lu_backend_init() is idempotent and refcounted. */
LU_API int  lu_backend_init(void);
LU_API void lu_backend_free(void);

/* Routes llama.cpp's internal log through __android_log_print (tag "llama_unity").
 * Off by default: llama.cpp is chatty and logcat writes are not free. */
LU_API void lu_set_log_to_android(int enable);

/* model_path is a UTF-8 filesystem path. The GGUF is mmap'd, so this returns
 * quickly and pages fault in during the first generation. Returns NULL on
 * failure; call lu_last_error(NULL) for the reason. */
LU_API lu_context* lu_load(const char* model_path, const lu_params* params);

/* Stops the worker, frees the context, model and sampler. Safe on NULL. */
LU_API void lu_free(lu_context* ctx);

/* Tokenizes text with the model's vocab. Writes at most out_cap ids into out.
 * Returns the token count, or a negative lu_result. Pass out=NULL/out_cap=0 to
 * count only — used to check a prompt against n_ctx before submitting. */
LU_API int32_t lu_tokenize(lu_context* ctx, const char* text,
                           int32_t* out, int32_t out_cap);

/* Detokenizes one id into buf. Returns bytes written (no NUL), or negative. */
LU_API int32_t lu_detokenize(lu_context* ctx, int32_t token,
                             char* buf, int32_t buf_cap);

/* Sets the system prompt and clears the conversation. Takes effect on the next
 * lu_submit(). Pass NULL or "" for no system prompt. */
LU_API int32_t lu_set_system_prompt(lu_context* ctx, const char* text);

/* Queues one user turn. The worker applies the model's own chat template to the
 * accumulated conversation, reuses the KV prefix it already holds, and streams
 * the reply. Returns LU_OK, or LU_ERR_BUSY if a job is still running.
 * max_tokens <= 0 means "until EOG or the context fills". */
LU_API int32_t lu_submit(lu_context* ctx, const char* user_text, int32_t max_tokens);

/* Copies up to buf_cap-1 bytes of pending output into buf and NUL-terminates it.
 * Never splits a UTF-8 sequence. Writes the byte count to out_written.
 * Returns the current lu_status, so one call per frame gets both the text and
 * the completion edge. */
LU_API int32_t lu_poll(lu_context* ctx, char* buf, int32_t buf_cap, int32_t* out_written);

/* Asks the worker to stop at the next token boundary. Returns immediately;
 * status becomes LU_CANCELLED once the worker unwinds. */
LU_API void lu_cancel(lu_context* ctx);

/* Drops the conversation and the KV cache. The system prompt survives. */
LU_API void lu_reset(lu_context* ctx);

/* Adjusts the post-token sleep while a job is in flight. This is the live
 * thermal knob: raising it lowers tokens/sec and lowers sustained SoC load, so
 * the compositor keeps its headroom instead of the whole chip throttling.
 * Safe to call from the Unity main thread at any time. */
LU_API void lu_set_throttle(lu_context* ctx, int32_t inter_token_delay_us);

LU_API int32_t lu_status_of(const lu_context* ctx);

/* Last error for ctx, or the last load error when ctx is NULL. Never NULL.
 * The returned pointer is owned by the library and valid until the next call. */
LU_API const char* lu_last_error(const lu_context* ctx);

/* Throughput of the most recent job. Any out pointer may be NULL. */
LU_API void lu_stats(const lu_context* ctx, float* prompt_tps, float* gen_tps,
                     int32_t* n_past, int32_t* n_ctx);

#ifdef __cplusplus
}
#endif
#endif /* LLAMA_UNITY_H */
