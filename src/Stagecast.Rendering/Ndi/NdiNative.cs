using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Stagecast.Rendering.Ndi;

/// <summary>
/// The few NDI 6 runtime calls we need. The runtime (Processing.NDI.Lib.x64.dll) is installed by NDI Tools / NDI Runtime from ndi.video
/// and is not shipped with this application; it is looked up at start. Struct layouts follow the NDI SDK headers for 64-bit Windows.
/// </summary>
internal static class NdiNative
{
    private const string Library = "Processing.NDI.Lib.x64";

    static NdiNative() => NativeLibrary.SetDllImportResolver(typeof(NdiNative).Assembly, Resolve);

    /// <summary>Folders searched for the runtime, in order. The environment variables are set by the NDI installers.</summary>
    internal static IEnumerable<string> SearchFolders()
    {
        foreach (string variable in new[] { "NDI_RUNTIME_DIR_V6", "NDI_RUNTIME_DIR_V5" })
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } dir) yield return dir;
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Path.Combine(programFiles, "NDI", "NDI 6 Runtime", "v6");
        yield return Path.Combine(programFiles, "NDI", "NDI 6 Tools", "Runtime");
        yield return Path.Combine(programFiles, "NDI", "NDI 5 Runtime");
        yield return Path.Combine(programFiles, "NDI", "NDI 5 Tools", "Runtime");
        yield return AppContext.BaseDirectory;
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Library) return IntPtr.Zero;
        foreach (string folder in SearchFolders())
        {
            string file = Path.Combine(folder, Library + ".dll");
            if (File.Exists(file) && NativeLibrary.TryLoad(file, out var handle)) return handle;
        }
        return IntPtr.Zero;
    }

    /// <summary>Null when the runtime can be loaded, else a message for the operator.</summary>
    public static string? CheckRuntime()
    {
        try
        {
            return initialize() ? null : "Runtime NDI tidak mau dijalankan (CPU atau sistem tidak didukung).";
        }
        catch (DllNotFoundException)
        {
            return "Runtime NDI tidak ditemukan. Pasang NDI Tools atau NDI Runtime dari ndi.video, lalu coba lagi.";
        }
        catch (EntryPointNotFoundException)
        {
            return "Runtime NDI yang terpasang terlalu lama. Pasang NDI 6 dari ndi.video.";
        }
    }

    // ---- common ----
    [DllImport(Library, EntryPoint = "NDIlib_initialize")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool initialize();

    // ---- sending ----
    [StructLayout(LayoutKind.Sequential)]
    public struct SendCreate
    {
        public IntPtr Name;   // UTF-8
        public IntPtr Groups;
        [MarshalAs(UnmanagedType.U1)] public bool ClockVideo;
        [MarshalAs(UnmanagedType.U1)] public bool ClockAudio;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct VideoFrame
    {
        public int Xres, Yres;
        public int FourCC;
        public int FrameRateN, FrameRateD;
        public float PictureAspectRatio;
        public int FrameFormatType; // 1 = progressive
        public long Timecode;
        public IntPtr Data;
        public int LineStrideInBytes;
        public IntPtr Metadata;
        public long Timestamp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct AudioFrame
    {
        public int SampleRate, NoChannels, NoSamples;
        public long Timecode;
        public IntPtr Data; // 32-bit float, planar
        public int ChannelStrideInBytes;
        public IntPtr Metadata;
        public long Timestamp;
    }

    public const long TimecodeSynthesize = long.MaxValue;
    public const int FourCcBgrx = 'B' | 'G' << 8 | 'R' << 16 | 'X' << 24;
    public const int FourCcBgra = 'B' | 'G' << 8 | 'R' << 16 | 'A' << 24;

    [DllImport(Library, EntryPoint = "NDIlib_send_create")]
    public static extern IntPtr send_create(ref SendCreate settings);

    [DllImport(Library, EntryPoint = "NDIlib_send_destroy")]
    public static extern void send_destroy(IntPtr instance);

    [DllImport(Library, EntryPoint = "NDIlib_send_send_video_v2")]
    public static extern void send_send_video_v2(IntPtr instance, ref VideoFrame frame);

    [DllImport(Library, EntryPoint = "NDIlib_send_send_audio_v2")]
    public static extern void send_send_audio_v2(IntPtr instance, ref AudioFrame frame);

    [DllImport(Library, EntryPoint = "NDIlib_send_get_no_connections")]
    public static extern int send_get_no_connections(IntPtr instance, uint timeoutMs);

    // ---- finding and receiving (used by the self test to look at our own output) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct FindCreate
    {
        [MarshalAs(UnmanagedType.U1)] public bool ShowLocalSources;
        public IntPtr Groups;
        public IntPtr ExtraIps;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Source
    {
        public IntPtr Name;    // UTF-8
        public IntPtr Address; // UTF-8 (union)
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RecvCreate
    {
        public Source SourceToConnectTo;
        public int ColorFormat;  // 0 = BGRX/BGRA
        public int Bandwidth;    // 100 = highest
        [MarshalAs(UnmanagedType.U1)] public bool AllowVideoFields;
        public IntPtr RecvName;
    }

    public const int FrameNone = 0, FrameVideo = 1, FrameAudio = 2;

    [DllImport(Library, EntryPoint = "NDIlib_find_create_v2")]
    public static extern IntPtr find_create_v2(ref FindCreate settings);

    [DllImport(Library, EntryPoint = "NDIlib_find_destroy")]
    public static extern void find_destroy(IntPtr instance);

    [DllImport(Library, EntryPoint = "NDIlib_find_wait_for_sources")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool find_wait_for_sources(IntPtr instance, uint timeoutMs);

    [DllImport(Library, EntryPoint = "NDIlib_find_get_current_sources")]
    public static extern IntPtr find_get_current_sources(IntPtr instance, out uint count);

    [DllImport(Library, EntryPoint = "NDIlib_recv_create_v3")]
    public static extern IntPtr recv_create_v3(ref RecvCreate settings);

    [DllImport(Library, EntryPoint = "NDIlib_recv_destroy")]
    public static extern void recv_destroy(IntPtr instance);

    [DllImport(Library, EntryPoint = "NDIlib_recv_capture_v2")]
    public static extern int recv_capture_v2(IntPtr instance, ref VideoFrame video, ref AudioFrame audio, IntPtr metadata, uint timeoutMs);

    [DllImport(Library, EntryPoint = "NDIlib_recv_free_video_v2")]
    public static extern void recv_free_video_v2(IntPtr instance, ref VideoFrame frame);

    [DllImport(Library, EntryPoint = "NDIlib_recv_free_audio_v2")]
    public static extern void recv_free_audio_v2(IntPtr instance, ref AudioFrame frame);
}
