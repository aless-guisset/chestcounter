using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ChestCounter;

/// <summary>
/// Enregistre le son d'un seul programme (et de ses sous-processus) en WAV, grâce à la capture « process loopback » de
/// Windows (Windows 10 2004+ / Windows 11) : seul le son de Fortnite (PC) ou de PS Remote Play (PS5) est capté, pas les
/// autres sons du PC, et même si ce programme est mis en sourdine dans le mélangeur. Sert à construire puis à faire
/// tourner la détection du son d'ouverture des coffres.
/// Format écrit : PCM 16 bits mono 16 kHz (≈ 115 Mo par heure).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AudioRecorder : IDisposable
{
    public const int SampleRate = 16000;
    private readonly int _pid;
    private readonly string _path;
    private Thread? _thread;
    private volatile bool _stop;
    private long _samples;

    public string Path => _path;
    public string? Error { get; private set; }
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Interlocked.Read(ref _samples) / SampleRate);
    public bool Running => _thread is { IsAlive: true };

    /// <summary>Appelé avec chaque bloc d'échantillons capté (mono, 16 kHz, -1..1) : pour la détection en direct.</summary>
    public Action<float[], int>? OnSamples { get; set; }

    public AudioRecorder(int pid, string path)
    {
        _pid = pid;
        _path = path;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "AudioRecorder" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        _stop = true;
        _thread?.Join(TimeSpan.FromSeconds(3));
    }

    public void Dispose() => Stop();

    private void Run()
    {
        FileStream? file = null;
        IAudioClient? client = null;
        try
        {
            client = Activate(_pid);
            var format = new WAVEFORMATEX { wFormatTag = 1, nChannels = 1, nSamplesPerSec = SampleRate, wBitsPerSample = 16, nBlockAlign = 2, nAvgBytesPerSec = SampleRate * 2 };
            const uint flags = AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK | AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            Check(client.Initialize(0, flags, 2_000_000, 0, ref format, IntPtr.Zero), "Initialize");
            using var ready = new AutoResetEvent(false);
            Check(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
            var iid = typeof(IAudioCaptureClient).GUID;
            Check(client.GetService(ref iid, out var svc), "GetService");
            var capture = (IAudioCaptureClient)svc;

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            file = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read);
            WriteHeader(file, 0);
            Check(client.Start(), "Start");

            var lastHeader = DateTime.UtcNow;
            var buffer = new byte[0];
            while (!_stop)
            {
                ready.WaitOne(200);
                while (capture.GetNextPacketSize(out var packet) == 0 && packet > 0)
                {
                    Check(capture.GetBuffer(out var data, out var frames, out var bufFlags, out _, out _), "GetBuffer");
                    var bytes = (int)frames * 2;
                    if (buffer.Length < bytes) buffer = new byte[bytes];
                    if ((bufFlags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) Array.Clear(buffer, 0, bytes);
                    else Marshal.Copy(data, buffer, 0, bytes);
                    capture.ReleaseBuffer(frames);
                    file.Write(buffer, 0, bytes);
                    Interlocked.Add(ref _samples, frames);
                    if (OnSamples is { } cb)
                    {
                        var f = new float[frames];
                        for (var i = 0; i < frames; i++) f[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;
                        try { cb(f, (int)frames); } catch (Exception ex) { ErrorLog.Write("Audio callback", ex); }
                    }
                }
                // L'en-tête est remis à jour régulièrement : le fichier reste lisible même si le compteur est coupé net.
                if (DateTime.UtcNow - lastHeader > TimeSpan.FromSeconds(10))
                {
                    lastHeader = DateTime.UtcNow;
                    FinishHeader(file);
                }
            }
            client.Stop();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            ErrorLog.Write("Audio", ex);
        }
        finally
        {
            if (file != null)
            {
                try { FinishHeader(file); } catch { }
                file.Dispose();
            }
            if (client != null) Marshal.ReleaseComObject(client);
        }
    }

    // ---- WAV ---------------------------------------------------------------------------------------------------

    private static void WriteHeader(Stream s, int dataBytes)
    {
        using var w = new BinaryWriter(s, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8.ToArray()); w.Write(36 + dataBytes); w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(SampleRate); w.Write(SampleRate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8.ToArray()); w.Write(dataBytes);
    }

    private static void FinishHeader(FileStream s)
    {
        var end = s.Position;
        var data = (int)Math.Min(int.MaxValue - 36, end - 44);
        s.Position = 0;
        WriteHeader(s, data);
        s.Position = end;
        s.Flush();
    }

    // ---- Activation « process loopback » -------------------------------------------------------------------------

    private static IAudioClient Activate(int pid)
    {
        var p = new AUDIOCLIENT_ACTIVATION_PARAMS { ActivationType = 1 /* PROCESS_LOOPBACK */, TargetProcessId = (uint)pid, ProcessLoopbackMode = 0 /* inclure l'arborescence */ };
        var paramsSize = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        var paramsPtr = Marshal.AllocHGlobal(paramsSize);
        var propPtr = Marshal.AllocHGlobal(24); // PROPVARIANT (VT_BLOB) en x64
        try
        {
            Marshal.StructureToPtr(p, paramsPtr, false);
            for (var i = 0; i < 24; i++) Marshal.WriteByte(propPtr, i, 0);
            Marshal.WriteInt16(propPtr, 0, 65); // VT_BLOB
            Marshal.WriteInt32(propPtr, 8, paramsSize);
            Marshal.WriteIntPtr(propPtr, 16, paramsPtr);
            var handler = new CompletionHandler();
            var iid = typeof(IAudioClient).GUID;
            Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", ref iid, propPtr, handler, out _), "ActivateAudioInterfaceAsync");
            if (!handler.Done.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("activation audio");
            Check(handler.Result, "activation audio");
            return (IAudioClient)handler.Client!;
        }
        finally
        {
            Marshal.FreeHGlobal(propPtr);
            Marshal.FreeHGlobal(paramsPtr);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} : 0x{hr:X8}");
    }

    private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    private const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid riid,
        IntPtr activationParams, IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, ref WAVEFORMATEX format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int result, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    /// <summary>Marqueur : l'objet peut être appelé depuis n'importe quel thread (exigé par l'activation asynchrone).</summary>
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ComVisible(true)]
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public int Result;
        public object? Client;

        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            try
            {
                var hr = operation.GetActivateResult(out Result, out Client);
                if (hr < 0) Result = hr;
            }
            catch (Exception ex)
            {
                Result = ex.HResult;
            }
            Done.Set();
            return 0;
        }
    }
}

/// <summary>
/// Surveille le programme dont on capte le son (Fortnite sur PC, PS Remote Play pour la PS5) et l'enregistre tant qu'il
/// tourne. Garde les derniers enregistrements dans &lt;données&gt;\audio.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AudioWatch
{
    /// <summary>Programmes captés selon la plateforme (noms de processus sans .exe).</summary>
    public static string[] Targets(bool console) => console
        ? new[] { "RemotePlay", "chiaki", "chiaki-ng" }
        : new[] { "FortniteClient-Win64-Shipping" };

    private const int Keep = 6;
    private static AudioRecorder? _rec;
    private static string? _target;
    private static string _folder = "";
    private static DateTime _nextCheck = DateTime.MinValue;

    public static AudioRecorder? Current => _rec;
    public static string? TargetName => _target;

    /// <summary>À appeler depuis la boucle principale (vérifie toutes les 5 s).</summary>
    public static void Tick(string dataDir, bool console, bool enabled)
    {
        if (DateTime.UtcNow < _nextCheck) return;
        _nextCheck = DateTime.UtcNow.AddSeconds(5);
        _folder = System.IO.Path.Combine(dataDir, "audio");
        var env = Environment.GetEnvironmentVariable("CHESTCOUNTER_AUDIO_PROCESS"); // tests
        var names = env is { Length: > 0 } ? new[] { env } : Targets(console);
        Process? proc = null;
        if (enabled)
        {
            foreach (var n in names)
            {
                var found = Process.GetProcessesByName(n);
                proc = found.OrderBy(p => { try { return p.StartTime; } catch { return DateTime.MaxValue; } }).FirstOrDefault();
                foreach (var p in found) if (p != proc) p.Dispose();
                if (proc != null) break;
            }
        }

        if (_rec != null && (proc == null || !_rec.Running))
        {
            var r = _rec;
            _rec = null;
            r.Stop();
            Console.ForegroundColor = r.Error != null ? ConsoleColor.Red : ConsoleColor.DarkGray;
            Console.WriteLine(r.Error != null
                ? L.T($"Son : enregistrement impossible ({r.Error}).", $"Sound: cannot record ({r.Error}).", $"Sonido: no se puede grabar ({r.Error}).")
                : L.T($"Son : enregistrement terminé ({(int)r.Duration.TotalMinutes} min) : {System.IO.Path.GetFileName(r.Path)}", $"Sound: recording finished ({(int)r.Duration.TotalMinutes} min): {System.IO.Path.GetFileName(r.Path)}", $"Sonido: grabación terminada ({(int)r.Duration.TotalMinutes} min): {System.IO.Path.GetFileName(r.Path)}"));
            Console.ResetColor();
            Cleanup();
        }
        if (_rec == null && proc != null)
        {
            _target = proc.ProcessName;
            var file = System.IO.Path.Combine(_folder, $"{(console ? "ps5" : "pc")}-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
            _rec = new AudioRecorder(proc.Id, file);
            _rec.Start();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(L.T($"Son : enregistrement de {_target} (pour la détection des coffres).", $"Sound: recording {_target} (for chest detection).", $"Sonido: grabando {_target} (para detectar los cofres)."));
            Console.ResetColor();
        }
        proc?.Dispose();
    }

    public static void StopAll()
    {
        _rec?.Stop();
        _rec = null;
    }

    /// <summary>Ne garde que les derniers enregistrements (ils pèsent ≈ 115 Mo par heure).</summary>
    private static void Cleanup()
    {
        try
        {
            foreach (var f in new DirectoryInfo(_folder).GetFiles("*.wav").OrderByDescending(f => f.LastWriteTimeUtc).Skip(Keep))
            {
                f.Delete();
            }
        }
        catch { }
    }

    /// <summary>--audio-test &lt;processus&gt; &lt;secondes&gt; &lt;fichier.wav&gt; : enregistre un programme puis quitte (développement).</summary>
    public static int Test(string name, int seconds, string path)
    {
        var p = int.TryParse(name, out var pid) ? Process.GetProcessById(pid) : Process.GetProcessesByName(name).FirstOrDefault();
        if (p == null) { Console.WriteLine($"processus introuvable : {name}"); return 1; }
        using var r = new AudioRecorder(p.Id, path);
        r.Start();
        Thread.Sleep(seconds * 1000);
        r.Stop();
        Console.WriteLine(r.Error ?? $"ok {r.Duration.TotalSeconds:0.0} s");
        return r.Error == null ? 0 : 2;
    }
}
