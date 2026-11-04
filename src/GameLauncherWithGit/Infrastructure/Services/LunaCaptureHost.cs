using GameLauncherWithGit.Application.Models;
using GameLauncherWithGit.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GameLauncherWithGit.Infrastructure.Services;

public sealed class LunaCaptureHost(ILogger<LunaCaptureHost> logger) : ILunaCaptureHost
{
    public event Action<CaptureSource, string>? Output;
    public event Action<uint, bool>? ConnectionChanged;
    private readonly List<Delegate> _callbacks = [];
    private nint _library;
    private bool _initialized;
    private Exception? _initializationError;
    private PidCall? _connect;
    private PidCall? _detach;
    private CheckInject? _check;
    private TaskCompletionSource? _connected;
    private int _target;

    [StructLayout(LayoutKind.Sequential)]
    private struct ThreadParam { public uint Pid; public ulong Address; public ulong Context; public ulong Context2; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ProcessEvent(uint pid);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CreateEvent(nint code, nint name, ThreadParam thread, [MarshalAs(UnmanagedType.I1)] bool embed);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ThreadEvent(nint code, nint name, ThreadParam thread);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void OutputEvent(nint code, nint name, ThreadParam thread, nint text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void InfoEvent(int type, nint text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void InsertEvent(uint pid, ulong address, nint code);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void EmbedEvent(nint text, ThreadParam thread);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint I18nEvent(nint text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void EmuEvent(nint id, nint title, nint version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Start(nint connect, nint disconnect, nint create, nint destroy, nint output, nint info, nint insert, nint embed, nint i18n, nint emu);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Settings(int delay, int codepage, int buffer, int history, [MarshalAs(UnmanagedType.I1)] bool pcHooks);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PidCall(uint pid);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)][return: MarshalAs(UnmanagedType.I1)] private delegate bool CheckInject(uint pid);

    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

    private void Initialize()
    {
        if (_initialized) return;
        if (_initializationError is not null) throw new InvalidOperationException("HOOKホストの初期化に失敗しています。", _initializationError);
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("HOOK取得はWindowsで利用できます。");
            if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.X86))
                throw new PlatformNotSupportedException("HOOK取得はx64/x86プロセスで利用できます。");
            var bits = Environment.Is64BitProcess ? 64 : 32;
            _library = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, "Luna", $"LunaHost{bits}.dll"));
            _connect = Export<PidCall>("Luna_ConnectProcess");
            _detach = Export<PidCall>("Luna_DetachProcess");
            _check = Export<CheckInject>("Luna_CheckIfNeedInject");
            _callbacks.AddRange([
                new ProcessEvent(pid => Guard(() => { if (pid == _target) _connected?.TrySetResult(); ConnectionChanged?.Invoke(pid, true); })),
            new ProcessEvent(pid => Guard(() => ConnectionChanged?.Invoke(pid, false))),
            new CreateEvent((_, _, _, _) => { }), new ThreadEvent((_, _, _) => { }),
            new OutputEvent((code, name, thread, text) => Guard(() => Output?.Invoke(
                new(thread.Pid, thread.Address, thread.Context, thread.Context2, Marshal.PtrToStringUni(code) ?? "", Marshal.PtrToStringUTF8(name) ?? ""),
                Marshal.PtrToStringUni(text) ?? ""))),
            new InfoEvent((type, _) => Guard(() => { if (type != 0) logger.LogDebug("Luna host notification. type={Type}", type); })),
            new InsertEvent((_, _, _) => { }), new EmbedEvent((_, _) => { }), new I18nEvent(_ => 0), new EmuEvent((_, _, _) => { })
            ]);
            var p = _callbacks.Select(Marshal.GetFunctionPointerForDelegate).ToArray();
            Export<Start>("Luna_Start")(p[0], p[1], p[2], p[3], p[4], p[5], p[6], p[7], p[8], p[9]);
            Export<Settings>("Luna_Settings")(500, 932, 3000, 20000, false);
            _initialized = true;
            // The native host owns background threads until process exit. Do not unload its DLL.
        }
        catch (Exception ex) { _initializationError = ex; throw; }
    }

    private void Guard(Action action)
    {
        try { action(); }
        catch (Exception ex) { logger.LogError(ex, "Luna callback failed."); }
    }

    public async Task ConnectAsync(int processId)
    {
        var bits = GetProcessBits(processId);
        var directory = Path.Combine(AppContext.BaseDirectory, "Luna");
        var injector = Path.Combine(directory, $"LunaInjector{bits}.exe");
        var hook = Path.Combine(directory, $"LunaHook{bits}.dll");
        if (!File.Exists(injector) || !File.Exists(hook)) throw new FileNotFoundException("HOOKの実行部品がありません。ネイティブビルドを確認してください。");
        Initialize();
        _target = processId;
        _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _connect!((uint)processId);
        if (!_check!((uint)processId)) throw new InvalidOperationException("対象はすでに別のHOOKホストへ接続されています。");
        using var helper = Process.Start(new ProcessStartInfo(injector)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { processId.ToString(), hook },
            WorkingDirectory = directory
        }) ?? throw new InvalidOperationException("注入helperを起動できませんでした。");
        await Task.WhenAll(helper.WaitForExitAsync(), _connected.Task).WaitAsync(TimeSpan.FromSeconds(15));
        if (helper.ExitCode != 0) throw new InvalidOperationException($"注入helperが失敗しました（{helper.ExitCode}）。");
    }

    public void Detach(int processId) => _detach?.Invoke((uint)processId);

    public IReadOnlyList<CaptureProcess> GetProcesses()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var result = new List<CaptureProcess>();
        foreach (var process in Process.GetProcesses())
            using (process)
            {
                try
                {
                    var handle = OpenProcess(0x1000, false, process.Id);
                    if (handle == 0) continue;
                    try
                    {
                        var path = new StringBuilder(32768); var size = path.Capacity;
                        if (QueryFullProcessImageName(handle, 0, path, ref size))
                            result.Add(new(process.Id, process.ProcessName, path.ToString(), process.MainWindowTitle));
                    }
                    finally { CloseHandle(handle); }
                }
                catch (InvalidOperationException) { }
            }
        return result.OrderBy(x => x.Name).ToArray();
    }

    private static int GetProcessBits(int id)
    {
        var handle = OpenProcess(0x1000, false, id);
        if (handle == 0) throw new InvalidOperationException("ゲーム本体の情報を取得できません。");
        try
        {
            if (!IsWow64Process2(handle, out var machine, out var native)) throw new InvalidOperationException("ゲーム本体のbit数を取得できません。");
            return (machine == 0 ? native : machine) switch { 0x14c => 32, 0x8664 => 64, _ => throw new PlatformNotSupportedException("対象のCPU形式には対応していません。") };
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(nint process, int flags, StringBuilder path, ref int size);
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);
}
