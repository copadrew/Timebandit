// LlamaNative.cs — raw P/Invoke surface for libllama_unity.so.
//
// Nothing in here allocates per-token or touches Unity types; that is
// LlamaRunner's job. Two marshalling rules are load-bearing on Android/IL2CPP:
//
//  1. Strings go across as byte[] of explicit UTF-8, never as `string`. The
//     default marshaller's idea of "ANSI" is platform-dependent, and a prompt
//     with an em dash or an emoji in it is exactly the input a user will try
//     first. Encoding it ourselves removes the ambiguity.
//
//  2. Returned `const char*` comes back as IntPtr and is copied immediately.
//     The native side owns that memory and may reuse it on the next call.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace QuestLlm.Native
{
    public enum LuStatus
    {
        Idle      = 0,
        Running   = 1,
        Done      = 2,
        Error     = 3,
        Cancelled = 4,
    }

    public enum LuResult
    {
        Ok            =  0,
        Generic       = -1,
        BadArg        = -2,
        LoadFailed    = -3,
        Busy          = -4,
        ContextFull   = -5,
    }

    /// Mirrors lu_params in llama_unity.h. Field order and types must match
    /// exactly — this is a blittable struct passed by pointer, so a mismatch
    /// is silent corruption rather than a compile error.
    [StructLayout(LayoutKind.Sequential)]
    public struct LuParams
    {
        public int   NCtx;
        public int   NThreads;
        public int   NBatch;
        public int   NGpuLayers;
        public uint  Seed;

        public float Temperature;
        public float TopP;
        public int   TopK;
        public float RepeatPenalty;
        public int   RepeatLastN;

        public uint  CpuMask;
        public int   InterTokenDelayUs;
        public int   MaxJobMs;
    }

    public static class LlamaNative
    {
        private const string Lib = "llama_unity";

        [DllImport(Lib, EntryPoint = "lu_defaults", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Defaults(ref LuParams p);

        [DllImport(Lib, EntryPoint = "lu_backend_init", CallingConvention = CallingConvention.Cdecl)]
        public static extern int BackendInit();

        [DllImport(Lib, EntryPoint = "lu_backend_free", CallingConvention = CallingConvention.Cdecl)]
        public static extern void BackendFree();

        [DllImport(Lib, EntryPoint = "lu_set_log_to_android", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetLogToAndroid(int enable);

        [DllImport(Lib, EntryPoint = "lu_load", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr LoadRaw(byte[] modelPath, ref LuParams p);

        [DllImport(Lib, EntryPoint = "lu_free", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Free(IntPtr ctx);

        [DllImport(Lib, EntryPoint = "lu_tokenize", CallingConvention = CallingConvention.Cdecl)]
        private static extern int TokenizeRaw(IntPtr ctx, byte[] text, int[] outIds, int outCap);

        [DllImport(Lib, EntryPoint = "lu_set_system_prompt", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SetSystemPromptRaw(IntPtr ctx, byte[] text);

        [DllImport(Lib, EntryPoint = "lu_submit", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SubmitRaw(IntPtr ctx, byte[] userText, int maxTokens);

        [DllImport(Lib, EntryPoint = "lu_poll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Poll(IntPtr ctx, byte[] buf, int bufCap, out int written);

        [DllImport(Lib, EntryPoint = "lu_cancel", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Cancel(IntPtr ctx);

        [DllImport(Lib, EntryPoint = "lu_reset", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Reset(IntPtr ctx);

        [DllImport(Lib, EntryPoint = "lu_set_throttle", CallingConvention = CallingConvention.Cdecl)]
        public static extern void SetThrottle(IntPtr ctx, int interTokenDelayUs);

        [DllImport(Lib, EntryPoint = "lu_status_of", CallingConvention = CallingConvention.Cdecl)]
        public static extern int StatusOf(IntPtr ctx);

        [DllImport(Lib, EntryPoint = "lu_last_error", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr LastErrorRaw(IntPtr ctx);

        [DllImport(Lib, EntryPoint = "lu_stats", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Stats(IntPtr ctx, out float promptTps, out float genTps,
                                        out int nPast, out int nCtx);

        // --- managed-friendly wrappers -------------------------------------

        private static byte[] Utf8Z(string s)
        {
            if (string.IsNullOrEmpty(s)) return new byte[] { 0 };
            int n = Encoding.UTF8.GetByteCount(s);
            var buf = new byte[n + 1];
            Encoding.UTF8.GetBytes(s, 0, s.Length, buf, 0);
            buf[n] = 0;
            return buf;
        }

        public static IntPtr Load(string modelPath, ref LuParams p)
            => LoadRaw(Utf8Z(modelPath), ref p);

        public static int Tokenize(IntPtr ctx, string text, int[] outIds, int outCap)
            => TokenizeRaw(ctx, Utf8Z(text), outIds, outCap);

        /// Token count without materialising the ids — use this to check a
        /// prompt against the context window before submitting it.
        public static int CountTokens(IntPtr ctx, string text)
            => TokenizeRaw(ctx, Utf8Z(text), null, 0);

        public static LuResult SetSystemPrompt(IntPtr ctx, string text)
            => (LuResult) SetSystemPromptRaw(ctx, Utf8Z(text));

        public static LuResult Submit(IntPtr ctx, string userText, int maxTokens)
            => (LuResult) SubmitRaw(ctx, Utf8Z(userText), maxTokens);

        public static string LastError(IntPtr ctx)
        {
            IntPtr p = LastErrorRaw(ctx);
            return p == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUTF8(p) ?? string.Empty);
        }
    }
}
