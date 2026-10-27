using System.Runtime.InteropServices;

namespace Stagecast.Spike;

/// <summary>
/// WASAPI "process loopback": captures everything a process (and its child processes) plays, without touching the
/// audio device and without hearing other applications. WebView2 plays through child processes, so we target our own
/// process tree. Windows 10 2004+ / Windows 11. There is no managed wrapper for this API, hence the interop here.
/// </summary>
internal sealed class ProcessLoopbackCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;

    private readonly Action<float[], int> _onSamples;
    private readonly Thread _thread;
    private volatile bool _stop;
    private IAudioClient? _client;
    private IAudioCaptureClient? _capture;

    /// <param name="onSamples">Called on the capture thread with interleaved 32-bit float stereo samples and the frame count.</param>
    public ProcessLoopbackCapture(int processId, Action<float[], int> onSamples)
    {
        _onSamples = onSamples;
        var ready = new ManualResetEventSlim();
        Exception? failure = null;
        _thread = new Thread(() =>
        {
            try
            {
                Open(processId);
                ready.Set();
                Pump();
            }
            catch (Exception ex)
            {
                failure = ex;
                ready.Set();
            }
        }) { IsBackground = true, Name = "process-loopback" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        ready.Wait();
        if (failure is not null) throw new InvalidOperationException($"process loopback failed: {failure.Message}", failure);
    }

    private void Open(int processId)
    {
        var activation = new AudioClientActivationParams
        {
            ActivationType = 1, // PROCESS_LOOPBACK
            TargetProcessId = (uint)processId,
            ProcessLoopbackMode = 0, // INCLUDE_TARGET_PROCESS_TREE
        };
        int size = Marshal.SizeOf<AudioClientActivationParams>();
        IntPtr paramsPtr = Marshal.AllocHGlobal(size);
        IntPtr variantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(activation, paramsPtr, false);
            Marshal.StructureToPtr(new PropVariantBlob { vt = 65 /* VT_BLOB */, cbSize = (uint)size, pBlobData = paramsPtr }, variantPtr, false);

            var handler = new CompletionHandler();
            Guid iid = typeof(IAudioClient).GUID;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, variantPtr, handler, out var operation));
            if (!handler.Done.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("ActivateAudioInterfaceAsync did not complete");
            Marshal.ThrowExceptionForHR(operation.GetActivateResult(out int hr, out object client));
            Marshal.ThrowExceptionForHR(hr);
            _client = (IAudioClient)client;
        }
        finally
        {
            Marshal.FreeHGlobal(variantPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }

        var format = new WaveFormatEx
        {
            wFormatTag = 3, // IEEE float
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = 32,
            nBlockAlign = Channels * 4,
            nAvgBytesPerSec = SampleRate * Channels * 4,
            cbSize = 0,
        };
        const uint loopback = 0x00020000, autoConvert = 0x80000000, srcDefaultQuality = 0x08000000;
        Marshal.ThrowExceptionForHR(_client!.Initialize(0 /* shared */, loopback | autoConvert | srcDefaultQuality, 1_000_000, 0, ref format, IntPtr.Zero));
        Guid captureIid = typeof(IAudioCaptureClient).GUID;
        Marshal.ThrowExceptionForHR(_client.GetService(ref captureIid, out object capture));
        _capture = (IAudioCaptureClient)capture;
        Marshal.ThrowExceptionForHR(_client.Start());
    }

    private void Pump()
    {
        var buffer = new float[SampleRate * Channels]; // 1 s of headroom
        while (!_stop)
        {
            Thread.Sleep(10);
            while (!_stop && _capture!.GetNextPacketSize(out uint packet) == 0 && packet > 0)
            {
                Marshal.ThrowExceptionForHR(_capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _));
                int samples = (int)frames * Channels;
                if (samples > buffer.Length) buffer = new float[samples];
                if ((flags & 2) != 0) Array.Clear(buffer, 0, samples); // AUDCLNT_BUFFERFLAGS_SILENT
                else Marshal.Copy(data, buffer, 0, samples);
                _capture.ReleaseBuffer(frames);
                _onSamples(buffer, (int)frames);
            }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
        _client?.Stop();
        if (_capture is not null) Marshal.ReleaseComObject(_capture);
        if (_client is not null) Marshal.ReleaseComObject(_client);
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AudioClientActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    // PROPVARIANT holding a BLOB: 8 bytes of type/reserved, then { ULONG cbSize; BYTE* pBlobData } aligned to the pointer size.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariantBlob
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public uint cbSize;
        [FieldOffset(16)] public IntPtr pBlobData;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public ManualResetEventSlim Done { get; } = new();
        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
        {
            Done.Set();
            return 0;
        }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WaveFormatEx format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }
}
