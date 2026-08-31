// WhisperNative.cs — raw P/Invoke surface for libwhisper_unity.so.
// Same marshalling rules as LlamaNative.cs.

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace QuestLlm.Native
{
    public enum WuStatus
    {
        Idle    = 0,
        Running = 1,
        Done    = 2,
        Error   = 3,
    }

    /// Mirrors wu_params in whisper_unity.h.
    /// `Language` is a fixed 8-byte char array in C, so it is declared as a
    /// fixed-size ByValTStr here rather than a string field.
    [StructLayout(LayoutKind.Sequential)]
    public struct WuParams
    {
        public int  NThreads;
        public uint CpuMask;
        public int  Translate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 8)]
        public string Language;
    }

    public static class WhisperNative
    {
        private const string Lib = "whisper_unity";

        /// whisper.cpp only accepts 16 kHz mono float audio. Unity's Microphone
        /// almost never hands you that, so VoiceInput resamples to this rate.
        public const int SampleRate = 16000;

        [DllImport(Lib, EntryPoint = "wu_defaults", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Defaults(ref WuParams p);

        [DllImport(Lib, EntryPoint = "wu_load", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr LoadRaw(byte[] modelPath, ref WuParams p);

        [DllImport(Lib, EntryPoint = "wu_free", CallingConvention = CallingConvention.Cdecl)]
        public static extern void Free(IntPtr ctx);

        [DllImport(Lib, EntryPoint = "wu_submit", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Submit(IntPtr ctx, float[] samples, int nSamples);

        [DllImport(Lib, EntryPoint = "wu_poll", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Poll(IntPtr ctx, byte[] buf, int bufCap, out int written);

        [DllImport(Lib, EntryPoint = "wu_status_of", CallingConvention = CallingConvention.Cdecl)]
        public static extern int StatusOf(IntPtr ctx);

        [DllImport(Lib, EntryPoint = "wu_last_error", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr LastErrorRaw(IntPtr ctx);

        public static IntPtr Load(string modelPath, ref WuParams p)
        {
            int n = Encoding.UTF8.GetByteCount(modelPath);
            var buf = new byte[n + 1];
            Encoding.UTF8.GetBytes(modelPath, 0, modelPath.Length, buf, 0);
            buf[n] = 0;
            return LoadRaw(buf, ref p);
        }

        public static string LastError(IntPtr ctx)
        {
            IntPtr p = LastErrorRaw(ctx);
            return p == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUTF8(p) ?? string.Empty);
        }
    }
}
