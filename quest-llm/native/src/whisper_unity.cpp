// whisper_unity.cpp — see include/whisper_unity.h for the contract.

#include "whisper_unity.h"

#include "whisper.h"

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#if defined(__ANDROID__)
#  include <android/log.h>
#  include <sched.h>
#  define WU_LOGI(...) __android_log_print(ANDROID_LOG_INFO,  "whisper_unity", __VA_ARGS__)
#  define WU_LOGE(...) __android_log_print(ANDROID_LOG_ERROR, "whisper_unity", __VA_ARGS__)
#else
#  define WU_LOGI(...) do { fprintf(stderr, __VA_ARGS__); fputc('\n', stderr); } while (0)
#  define WU_LOGE(...) do { fprintf(stderr, __VA_ARGS__); fputc('\n', stderr); } while (0)
#endif

static std::string g_load_error;

struct wu_context {
    whisper_context * wctx = nullptr;
    wu_params params{};

    std::thread             worker;
    std::mutex              mu;
    std::condition_variable cv;
    std::atomic<bool>       shutdown{false};
    std::atomic<bool>       has_job{false};
    std::atomic<int32_t>    status{WU_IDLE};

    std::vector<float> pending_audio;   // guarded by mu
    std::string        transcript;      // guarded by mu
    std::string        last_error;      // guarded by mu
};

void wu_defaults(wu_params * p) {
    if (p == nullptr) return;
    p->n_threads = 2;          // whisper is bursty; keep it off the LLM's cores
    p->cpu_mask  = 0x0C;       // cores 2,3 by default — distinct from the LLM's
    p->translate = 0;
    std::snprintf(p->language, sizeof(p->language), "%s", "en");
}

static void run_job(wu_context * c, std::vector<float> & audio) {
    whisper_full_params wp = whisper_full_default_params(WHISPER_SAMPLING_GREEDY);
    wp.n_threads         = c->params.n_threads;
    wp.translate         = c->params.translate != 0;
    wp.language          = c->params.language;
    wp.print_progress    = false;
    wp.print_realtime    = false;
    wp.print_timestamps  = false;
    wp.print_special     = false;
    wp.no_context        = true;   // each utterance is independent
    wp.single_segment    = false;
    wp.suppress_nst      = true;   // drop "(wind blowing)" style non-speech tags

    if (whisper_full(c->wctx, wp, audio.data(), (int) audio.size()) != 0) {
        std::lock_guard<std::mutex> lk(c->mu);
        c->last_error = "whisper_full failed";
        c->status.store(WU_ERROR);
        WU_LOGE("whisper_full failed");
        return;
    }

    std::string text;
    const int n = whisper_full_n_segments(c->wctx);
    for (int i = 0; i < n; ++i) {
        const char * seg = whisper_full_get_segment_text(c->wctx, i);
        if (seg != nullptr) text += seg;
    }

    // whisper likes to prefix a space; the LLM does not care but the UI does.
    const size_t first = text.find_first_not_of(" \t\n\r");
    const size_t last  = text.find_last_not_of(" \t\n\r");
    text = (first == std::string::npos) ? std::string() : text.substr(first, last - first + 1);

    {
        std::lock_guard<std::mutex> lk(c->mu);
        c->transcript = std::move(text);
    }
    c->status.store(WU_DONE);
}

static void worker_main(wu_context * c) {
#if defined(__ANDROID__)
    if (c->params.cpu_mask != 0) {
        cpu_set_t set;
        CPU_ZERO(&set);
        for (int i = 0; i < 32; ++i) {
            if (c->params.cpu_mask & (1u << i)) CPU_SET(i, &set);
        }
        if (sched_setaffinity(0, sizeof(set), &set) != 0) {
            WU_LOGE("sched_setaffinity(0x%x) failed; running unpinned", c->params.cpu_mask);
        }
    }
#endif
    for (;;) {
        std::vector<float> audio;
        {
            std::unique_lock<std::mutex> lk(c->mu);
            c->cv.wait(lk, [c] { return c->has_job.load() || c->shutdown.load(); });
            if (c->shutdown.load()) return;
            audio.swap(c->pending_audio);
            c->has_job.store(false);
        }
        run_job(c, audio);
    }
}

wu_context * wu_load(const char * model_path, const wu_params * params_in) {
    if (model_path == nullptr || params_in == nullptr) {
        g_load_error = "wu_load: null argument";
        return nullptr;
    }

    auto * c = new wu_context();
    c->params = *params_in;

    whisper_context_params cp = whisper_context_default_params();
    cp.use_gpu = false;   // the GPU belongs to the compositor; see README

    c->wctx = whisper_init_from_file_with_params(model_path, cp);
    if (c->wctx == nullptr) {
        g_load_error = std::string("wu_load: could not load '") + model_path + "'";
        WU_LOGE("%s", g_load_error.c_str());
        delete c;
        return nullptr;
    }

    c->status.store(WU_IDLE);
    c->worker = std::thread(worker_main, c);
    WU_LOGI("loaded whisper model '%s'", model_path);
    return c;
}

void wu_free(wu_context * c) {
    if (c == nullptr) return;
    c->shutdown.store(true);
    c->cv.notify_all();
    if (c->worker.joinable()) c->worker.join();
    if (c->wctx) whisper_free(c->wctx);
    delete c;
}

int32_t wu_submit(wu_context * c, const float * samples, int32_t n_samples) {
    if (c == nullptr || samples == nullptr || n_samples <= 0) return WU_ERR_BAD_ARG;
    if (c->status.load() == WU_RUNNING || c->has_job.load())   return WU_ERR_BUSY;

    {
        std::lock_guard<std::mutex> lk(c->mu);
        c->pending_audio.assign(samples, samples + n_samples);
        c->transcript.clear();
        c->last_error.clear();
        c->has_job.store(true);
    }
    c->status.store(WU_RUNNING);
    c->cv.notify_one();
    return WU_OK;
}

int32_t wu_poll(wu_context * c, char * buf, int32_t buf_cap, int32_t * out_written) {
    if (out_written != nullptr) *out_written = 0;
    if (c == nullptr) return WU_ERR_BAD_ARG;

    const int32_t st = c->status.load();
    if (st != WU_DONE || buf == nullptr || buf_cap <= 1) return st;

    std::lock_guard<std::mutex> lk(c->mu);
    const int32_t n = std::min<int32_t>((int32_t) c->transcript.size(), buf_cap - 1);
    std::memcpy(buf, c->transcript.data(), (size_t) n);
    buf[n] = '\0';
    if (out_written != nullptr) *out_written = n;

    c->transcript.clear();
    c->status.store(WU_IDLE);   // edge consumed
    return WU_DONE;
}

int32_t wu_status_of(const wu_context * c) {
    return (c == nullptr) ? WU_ERR_BAD_ARG : c->status.load();
}

const char * wu_last_error(const wu_context * c) {
    return (c == nullptr) ? g_load_error.c_str() : c->last_error.c_str();
}
