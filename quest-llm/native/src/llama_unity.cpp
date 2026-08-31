// llama_unity.cpp — see include/llama_unity.h for the contract.

#include "llama_unity.h"
#include "lu_utf8.h"

#include "llama.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#if defined(__ANDROID__)
#  include <android/log.h>
#  include <sched.h>
#  define LU_LOGI(...) __android_log_print(ANDROID_LOG_INFO,  "llama_unity", __VA_ARGS__)
#  define LU_LOGE(...) __android_log_print(ANDROID_LOG_ERROR, "llama_unity", __VA_ARGS__)
#else
#  define LU_LOGI(...) do { fprintf(stderr, __VA_ARGS__); fputc('\n', stderr); } while (0)
#  define LU_LOGE(...) do { fprintf(stderr, __VA_ARGS__); fputc('\n', stderr); } while (0)
#endif

// ---------------------------------------------------------------------------
// llama.cpp API compatibility.
//
// llama.h has no version macro, so if you pin an older tag than the one in
// CMakeLists.txt this block is the only place that needs editing. The names on
// the left are what the rest of this file uses.
// ---------------------------------------------------------------------------
namespace compat {

static inline llama_model * model_load(const char * path, llama_model_params p) {
    // Pre-Jan-2025 tags: llama_load_model_from_file(path, p)
    return llama_model_load_from_file(path, p);
}

static inline llama_context * context_new(llama_model * m, llama_context_params p) {
    // Pre-Jan-2025 tags: llama_new_context_with_model(m, p)
    return llama_init_from_model(m, p);
}

static inline void model_free(llama_model * m) {
    // Pre-Jan-2025 tags: llama_free_model(m)
    llama_model_free(m);
}

static inline const llama_vocab * vocab_of(const llama_model * m) {
    // Pre-Dec-2024 tags: the vocab-taking calls took the model directly.
    return llama_model_get_vocab(m);
}

static inline void kv_clear(llama_context * c) {
    // Pre-2025 tags: llama_kv_cache_clear(c)
    llama_memory_clear(llama_get_memory(c), true);
}

static inline void kv_seq_rm(llama_context * c, llama_seq_id seq,
                             llama_pos p0, llama_pos p1) {
    // Pre-2025 tags: llama_kv_cache_seq_rm(c, seq, p0, p1)
    llama_memory_seq_rm(llama_get_memory(c), seq, p0, p1);
}

} // namespace compat

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

static std::string g_load_error;
static std::mutex  g_load_error_mu;

static void set_load_error(const char * fmt, ...) {
    char buf[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(buf, sizeof(buf), fmt, ap);
    va_end(ap);
    std::lock_guard<std::mutex> lk(g_load_error_mu);
    g_load_error = buf;
    LU_LOGE("%s", buf);
}

// ---------------------------------------------------------------------------
// Context
// ---------------------------------------------------------------------------

struct chat_turn {
    std::string role;     // "system" | "user" | "assistant"
    std::string content;
};

struct lu_context {
    llama_model   * model = nullptr;
    llama_context * lctx  = nullptr;
    const llama_vocab * vocab = nullptr;
    llama_sampler * smpl  = nullptr;

    lu_params params{};
    std::string chat_template;   // resolved once at load

    // Worker
    std::thread             worker;
    std::mutex              mu;
    std::condition_variable cv;
    std::atomic<bool>       shutdown{false};
    std::atomic<bool>       has_job{false};
    std::atomic<bool>       cancel_flag{false};
    std::atomic<int32_t>    status{LU_IDLE};

    // Job input (guarded by mu)
    std::string pending_user;
    int32_t     pending_max_tokens = 0;

    // Output byte queue (guarded by mu)
    std::string out;

    // Conversation (worker-thread only after submit hands off)
    std::string             system_prompt;
    std::vector<chat_turn>  history;

    // Tokens currently resident in the KV cache, so we can reuse the prefix
    // across turns instead of re-evaluating the whole conversation each time.
    std::vector<llama_token> kv_tokens;

    // Live thermal knob, written from the Unity main thread, read by the worker
    // between tokens. Separate from params so it can change mid-job.
    std::atomic<int32_t> throttle_us{0};

    std::string last_error;      // guarded by mu
    std::atomic<int32_t> n_past{0};
    std::atomic<float>   prompt_tps{0.0f};
    std::atomic<float>   gen_tps{0.0f};

    void set_error(const std::string & msg) {
        std::lock_guard<std::mutex> lk(mu);
        last_error = msg;
        LU_LOGE("%s", msg.c_str());
    }

    void push_out(const char * data, size_t n) {
        std::lock_guard<std::mutex> lk(mu);
        out.append(data, n);
    }
};

// ---------------------------------------------------------------------------
// Backend
// ---------------------------------------------------------------------------

static std::mutex g_backend_mu;
static int        g_backend_refs = 0;
static bool       g_log_to_android = false;

static void lu_log_cb(ggml_log_level level, const char * text, void * /*user*/) {
    if (!g_log_to_android || text == nullptr) return;
#if defined(__ANDROID__)
    const int prio = (level == GGML_LOG_LEVEL_ERROR) ? ANDROID_LOG_ERROR
                   : (level == GGML_LOG_LEVEL_WARN)  ? ANDROID_LOG_WARN
                                                     : ANDROID_LOG_INFO;
    __android_log_write(prio, "llama_cpp", text);
#else
    (void) level;
    fputs(text, stderr);
#endif
}

void lu_set_log_to_android(int enable) {
    g_log_to_android = (enable != 0);
}

int lu_backend_init(void) {
    std::lock_guard<std::mutex> lk(g_backend_mu);
    if (g_backend_refs++ == 0) {
        llama_log_set(lu_log_cb, nullptr);
        llama_backend_init();
    }
    return LU_OK;
}

void lu_backend_free(void) {
    std::lock_guard<std::mutex> lk(g_backend_mu);
    if (g_backend_refs > 0 && --g_backend_refs == 0) {
        llama_backend_free();
    }
}

void lu_defaults(lu_params * p) {
    if (p == nullptr) return;
    p->n_ctx                = 2048;
    p->n_threads            = 3;
    p->n_batch              = 128;
    p->n_gpu_layers         = 0;
    p->seed                 = 0xFFFFFFFFu;
    p->temperature          = 0.7f;
    p->top_p                = 0.92f;
    p->top_k                = 40;
    p->repeat_penalty       = 1.08f;
    p->repeat_last_n        = 128;
    p->cpu_mask             = 0x70;   // cores 4,5,6 — measure on your device
    p->inter_token_delay_us = 0;
    p->max_job_ms           = 0;
}

// ---------------------------------------------------------------------------
// Prompt assembly
// ---------------------------------------------------------------------------

// Renders the conversation with the model's own chat template. Getting this
// wrong is the single most common cause of "the model outputs garbage" — an
// instruct model fed a raw string will happily ramble past its stop tokens.
static bool apply_chat_template(lu_context * c, bool add_assistant_prefix,
                                std::string & out_text) {
    std::vector<llama_chat_message> msgs;
    msgs.reserve(c->history.size() + 1);

    if (!c->system_prompt.empty()) {
        msgs.push_back({ "system", c->system_prompt.c_str() });
    }
    for (const auto & t : c->history) {
        msgs.push_back({ t.role.c_str(), t.content.c_str() });
    }
    if (msgs.empty()) return false;

    const char * tmpl = c->chat_template.empty() ? nullptr : c->chat_template.c_str();

    std::vector<char> buf(4096);
    int32_t n = llama_chat_apply_template(tmpl, msgs.data(), msgs.size(),
                                          add_assistant_prefix, buf.data(),
                                          (int32_t) buf.size());
    if (n > (int32_t) buf.size()) {
        buf.resize(n + 1);
        n = llama_chat_apply_template(tmpl, msgs.data(), msgs.size(),
                                      add_assistant_prefix, buf.data(),
                                      (int32_t) buf.size());
    }
    if (n < 0) return false;

    out_text.assign(buf.data(), (size_t) n);
    return true;
}

static std::vector<llama_token> tokenize(lu_context * c, const std::string & text,
                                         bool add_special) {
    // Negative return means "needed this many"; call twice.
    int32_t n = -llama_tokenize(c->vocab, text.c_str(), (int32_t) text.size(),
                                nullptr, 0, add_special, /*parse_special=*/true);
    if (n <= 0) return {};
    std::vector<llama_token> ids((size_t) n);
    const int32_t got = llama_tokenize(c->vocab, text.c_str(), (int32_t) text.size(),
                                       ids.data(), n, add_special, /*parse_special=*/true);
    if (got < 0) return {};
    ids.resize((size_t) got);
    return ids;
}

static std::string token_to_piece(lu_context * c, llama_token id) {
    char buf[256];
    const int32_t n = llama_token_to_piece(c->vocab, id, buf, (int32_t) sizeof(buf),
                                           /*lstrip=*/0, /*special=*/false);
    if (n <= 0) return {};
    return std::string(buf, (size_t) n);
}

// ---------------------------------------------------------------------------
// Generation
// ---------------------------------------------------------------------------

static void run_job(lu_context * c, const std::string & user_text, int32_t max_tokens) {
    using clock = std::chrono::steady_clock;
    const auto job_start = clock::now();

    c->history.push_back({ "user", user_text });

    // Build the full prompt, dropping the oldest exchanges until it fits. We
    // reserve room for the reply itself, otherwise a prompt that just fits
    // leaves nowhere to generate into.
    const int32_t reserve = std::max(64, max_tokens > 0 ? max_tokens : 256);
    std::vector<llama_token> full;
    for (;;) {
        std::string prompt;
        if (!apply_chat_template(c, /*add_assistant_prefix=*/true, prompt)) {
            c->set_error("failed to apply chat template");
            c->status.store(LU_ERROR);
            return;
        }
        full = tokenize(c, prompt, /*add_special=*/true);
        if (full.empty()) {
            c->set_error("tokenizer produced no tokens");
            c->status.store(LU_ERROR);
            return;
        }
        if ((int32_t) full.size() + reserve <= c->params.n_ctx) break;

        // Drop the oldest user/assistant pair. If only the current turn is
        // left, the single message itself is too large — nothing to trim.
        if (c->history.size() <= 1) {
            c->set_error("prompt does not fit in n_ctx even on its own");
            c->status.store(LU_ERROR);
            return;
        }
        c->history.erase(c->history.begin(),
                         c->history.begin() + std::min<size_t>(2, c->history.size() - 1));
        // The cache no longer matches the trimmed conversation.
        compat::kv_clear(c->lctx);
        c->kv_tokens.clear();
    }

    // Reuse whatever prefix of the KV cache still matches.
    size_t reuse = 0;
    const size_t max_reuse = std::min(c->kv_tokens.size(), full.size());
    while (reuse < max_reuse && c->kv_tokens[reuse] == full[reuse]) ++reuse;

    // Never reuse the entire prompt — llama_decode needs at least one token to
    // produce logits for the first sampled token.
    if (reuse == full.size()) reuse = full.size() - 1;

    if (reuse < c->kv_tokens.size()) {
        compat::kv_seq_rm(c->lctx, 0, (llama_pos) reuse, -1);
        c->kv_tokens.resize(reuse);
    }

    // --- prompt eval -------------------------------------------------------
    const auto prompt_start = clock::now();
    const int32_t n_prompt = (int32_t) (full.size() - reuse);

    for (size_t i = reuse; i < full.size(); i += (size_t) c->params.n_batch) {
        if (c->cancel_flag.load()) { c->status.store(LU_CANCELLED); return; }

        const int32_t n = (int32_t) std::min((size_t) c->params.n_batch, full.size() - i);
        llama_batch batch = llama_batch_get_one(full.data() + i, n);
        if (llama_decode(c->lctx, batch) != 0) {
            c->set_error("llama_decode failed during prompt evaluation");
            c->status.store(LU_ERROR);
            return;
        }
        c->kv_tokens.insert(c->kv_tokens.end(), full.begin() + i, full.begin() + i + n);
        c->n_past.store((int32_t) c->kv_tokens.size());
    }

    {
        const double secs = std::chrono::duration<double>(clock::now() - prompt_start).count();
        c->prompt_tps.store(secs > 0.0 ? (float) (n_prompt / secs) : 0.0f);
    }

    // --- token loop --------------------------------------------------------
    const auto gen_start = clock::now();
    int32_t n_generated = 0;
    std::string reply;

    const int32_t budget = (max_tokens > 0)
        ? max_tokens
        : (c->params.n_ctx - (int32_t) c->kv_tokens.size());

    for (int32_t i = 0; i < budget; ++i) {
        if (c->cancel_flag.load()) { c->status.store(LU_CANCELLED); break; }

        if (c->params.max_job_ms > 0) {
            const auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                                clock::now() - job_start).count();
            if (ms >= c->params.max_job_ms) break;
        }

        // llama_sampler_sample() accepts the token into the chain itself, so
        // there is no separate llama_sampler_accept() call here.
        const llama_token id = llama_sampler_sample(c->smpl, c->lctx, -1);

        if (llama_vocab_is_eog(c->vocab, id)) break;

        const std::string piece = token_to_piece(c, id);
        if (!piece.empty()) {
            reply += piece;
            c->push_out(piece.data(), piece.size());
        }

        c->kv_tokens.push_back(id);
        c->n_past.store((int32_t) c->kv_tokens.size());
        ++n_generated;

        if ((int32_t) c->kv_tokens.size() >= c->params.n_ctx) break;

        llama_batch batch = llama_batch_get_one(&c->kv_tokens.back(), 1);
        if (llama_decode(c->lctx, batch) != 0) {
            c->set_error("llama_decode failed during generation");
            c->status.store(LU_ERROR);
            break;
        }

        const int32_t throttle = c->throttle_us.load();
        if (throttle > 0) {
            std::this_thread::sleep_for(std::chrono::microseconds(throttle));
        }
    }

    {
        const double secs = std::chrono::duration<double>(clock::now() - gen_start).count();
        c->gen_tps.store(secs > 0.0 ? (float) (n_generated / secs) : 0.0f);
    }

    // Record what the model actually said, even on cancel, so the KV cache and
    // the conversation stay in agreement for the next turn.
    c->history.push_back({ "assistant", reply });

    const int32_t st = c->status.load();
    if (st == LU_RUNNING) c->status.store(LU_DONE);
}

static void worker_main(lu_context * c) {
#if defined(__ANDROID__)
    if (c->params.cpu_mask != 0) {
        cpu_set_t set;
        CPU_ZERO(&set);
        for (int i = 0; i < 32; ++i) {
            if (c->params.cpu_mask & (1u << i)) CPU_SET(i, &set);
        }
        if (sched_setaffinity(0, sizeof(set), &set) != 0) {
            LU_LOGE("sched_setaffinity(mask=0x%x) failed; running unpinned", c->params.cpu_mask);
        } else {
            LU_LOGI("worker pinned to cpu mask 0x%x", c->params.cpu_mask);
        }
    }
#endif

    for (;;) {
        std::string user_text;
        int32_t max_tokens = 0;
        {
            std::unique_lock<std::mutex> lk(c->mu);
            c->cv.wait(lk, [c] { return c->has_job.load() || c->shutdown.load(); });
            if (c->shutdown.load()) return;
            user_text.swap(c->pending_user);
            max_tokens = c->pending_max_tokens;
            c->has_job.store(false);
        }
        run_job(c, user_text, max_tokens);
    }
}

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

lu_context * lu_load(const char * model_path, const lu_params * params_in) {
    if (model_path == nullptr || params_in == nullptr) {
        set_load_error("lu_load: null argument");
        return nullptr;
    }
    lu_backend_init();

    auto * c = new lu_context();
    c->params = *params_in;

    llama_model_params mp = llama_model_default_params();
    mp.n_gpu_layers = c->params.n_gpu_layers;
    mp.use_mmap     = true;   // keeps resident set down; the OS can evict pages
    mp.use_mlock    = false;  // mlock on a headset with ~6 GB for everything: no

    c->model = compat::model_load(model_path, mp);
    if (c->model == nullptr) {
        set_load_error("lu_load: could not load GGUF at '%s'", model_path);
        delete c;
        return nullptr;
    }
    c->vocab = compat::vocab_of(c->model);

    llama_context_params cp = llama_context_default_params();
    cp.n_ctx     = (uint32_t) c->params.n_ctx;
    cp.n_batch   = (uint32_t) c->params.n_batch;
    cp.n_ubatch  = (uint32_t) c->params.n_batch;
    cp.n_threads = c->params.n_threads;
    cp.n_threads_batch = c->params.n_threads;

    c->lctx = compat::context_new(c->model, cp);
    if (c->lctx == nullptr) {
        set_load_error("lu_load: llama context creation failed (n_ctx=%d)", c->params.n_ctx);
        compat::model_free(c->model);
        delete c;
        return nullptr;
    }

    {
        const char * tmpl = llama_model_chat_template(c->model, /*name=*/nullptr);
        if (tmpl != nullptr) c->chat_template = tmpl;
        else LU_LOGI("model has no embedded chat template; falling back to llama.cpp default");
    }

    auto sp = llama_sampler_chain_default_params();
    sp.no_perf = true;
    c->smpl = llama_sampler_chain_init(sp);
    llama_sampler_chain_add(c->smpl, llama_sampler_init_penalties(
        c->params.repeat_last_n, c->params.repeat_penalty, 0.0f, 0.0f));
    llama_sampler_chain_add(c->smpl, llama_sampler_init_top_k(c->params.top_k));
    llama_sampler_chain_add(c->smpl, llama_sampler_init_top_p(c->params.top_p, 1));
    llama_sampler_chain_add(c->smpl, llama_sampler_init_temp(c->params.temperature));
    llama_sampler_chain_add(c->smpl, llama_sampler_init_dist(c->params.seed));

    c->throttle_us.store(c->params.inter_token_delay_us);
    c->status.store(LU_IDLE);
    c->worker = std::thread(worker_main, c);

    LU_LOGI("loaded '%s' (n_ctx=%d threads=%d mask=0x%x)",
            model_path, c->params.n_ctx, c->params.n_threads, c->params.cpu_mask);
    return c;
}

void lu_free(lu_context * c) {
    if (c == nullptr) return;

    c->cancel_flag.store(true);
    c->shutdown.store(true);
    c->cv.notify_all();
    if (c->worker.joinable()) c->worker.join();

    if (c->smpl)  llama_sampler_free(c->smpl);
    if (c->lctx)  llama_free(c->lctx);
    if (c->model) compat::model_free(c->model);

    delete c;
    lu_backend_free();
}

// ---------------------------------------------------------------------------
// Public surface
// ---------------------------------------------------------------------------

int32_t lu_tokenize(lu_context * c, const char * text, int32_t * out, int32_t out_cap) {
    if (c == nullptr || text == nullptr) return LU_ERR_BAD_ARG;
    const std::vector<llama_token> ids = tokenize(c, text, /*add_special=*/false);
    if (out != nullptr && out_cap > 0) {
        const int32_t n = std::min<int32_t>(out_cap, (int32_t) ids.size());
        std::memcpy(out, ids.data(), (size_t) n * sizeof(int32_t));
    }
    return (int32_t) ids.size();
}

int32_t lu_detokenize(lu_context * c, int32_t token, char * buf, int32_t buf_cap) {
    if (c == nullptr || buf == nullptr || buf_cap <= 0) return LU_ERR_BAD_ARG;
    const std::string piece = token_to_piece(c, (llama_token) token);
    const int32_t n = std::min<int32_t>(buf_cap, (int32_t) piece.size());
    std::memcpy(buf, piece.data(), (size_t) n);
    return n;
}

int32_t lu_set_system_prompt(lu_context * c, const char * text) {
    if (c == nullptr) return LU_ERR_BAD_ARG;
    if (c->status.load() == LU_RUNNING) return LU_ERR_BUSY;
    c->system_prompt = (text != nullptr) ? text : "";
    lu_reset(c);
    return LU_OK;
}

int32_t lu_submit(lu_context * c, const char * user_text, int32_t max_tokens) {
    if (c == nullptr || user_text == nullptr) return LU_ERR_BAD_ARG;
    if (c->status.load() == LU_RUNNING || c->has_job.load()) return LU_ERR_BUSY;

    {
        std::lock_guard<std::mutex> lk(c->mu);
        c->pending_user = user_text;
        c->pending_max_tokens = max_tokens;
        c->out.clear();
        c->last_error.clear();
        c->has_job.store(true);
    }
    c->cancel_flag.store(false);
    c->status.store(LU_RUNNING);
    c->cv.notify_one();
    return LU_OK;
}

int32_t lu_poll(lu_context * c, char * buf, int32_t buf_cap, int32_t * out_written) {
    if (out_written != nullptr) *out_written = 0;
    if (c == nullptr) return LU_ERR_BAD_ARG;
    if (buf == nullptr || buf_cap <= 1) return c->status.load();

    std::lock_guard<std::mutex> lk(c->mu);
    if (!c->out.empty()) {
        size_t n = std::min<size_t>(c->out.size(), (size_t) buf_cap - 1);
        n = lu_utf8_truncate(c->out.data(), n);
        if (n > 0) {
            std::memcpy(buf, c->out.data(), n);
            c->out.erase(0, n);
        }
        buf[n] = '\0';
        if (out_written != nullptr) *out_written = (int32_t) n;
    } else {
        buf[0] = '\0';
    }
    return c->status.load();
}

void lu_cancel(lu_context * c) {
    if (c == nullptr) return;
    c->cancel_flag.store(true);
}

void lu_set_throttle(lu_context * c, int32_t inter_token_delay_us) {
    if (c == nullptr) return;
    c->throttle_us.store(inter_token_delay_us < 0 ? 0 : inter_token_delay_us);
}

void lu_reset(lu_context * c) {
    if (c == nullptr) return;
    if (c->status.load() == LU_RUNNING) {
        c->cancel_flag.store(true);
        // The worker checks the flag between tokens; spin briefly rather than
        // ripping the KV cache out from under a live llama_decode.
        for (int i = 0; i < 500 && c->status.load() == LU_RUNNING; ++i) {
            std::this_thread::sleep_for(std::chrono::milliseconds(2));
        }
    }
    c->history.clear();
    c->kv_tokens.clear();
    if (c->lctx) compat::kv_clear(c->lctx);
    c->n_past.store(0);
    std::lock_guard<std::mutex> lk(c->mu);
    c->out.clear();
}

int32_t lu_status_of(const lu_context * c) {
    return (c == nullptr) ? LU_ERR_BAD_ARG : c->status.load();
}

const char * lu_last_error(const lu_context * c) {
    if (c == nullptr) {
        std::lock_guard<std::mutex> lk(g_load_error_mu);
        return g_load_error.c_str();
    }
    // Safe: last_error is only reassigned under mu, and the caller marshals a
    // copy before the next call can occur (single consumer thread, per header).
    return c->last_error.c_str();
}

void lu_stats(const lu_context * c, float * prompt_tps, float * gen_tps,
              int32_t * n_past, int32_t * n_ctx) {
    if (c == nullptr) return;
    if (prompt_tps) *prompt_tps = c->prompt_tps.load();
    if (gen_tps)    *gen_tps    = c->gen_tps.load();
    if (n_past)     *n_past     = c->n_past.load();
    if (n_ctx)      *n_ctx      = c->params.n_ctx;
}
