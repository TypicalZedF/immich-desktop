using System.Reflection;
using System.Runtime.InteropServices;

namespace ImmichDesktop.App.Services.Mpv;

/// <summary>
/// The parts of libmpv's C API the video player uses (client.h, render.h, render_gl.h of API 2.x).
/// Windows: libmpv-2.dll next to the app. Linux: the system's libmpv.so.2 (e.g. Fedora's mpv-libs).
/// </summary>
static class MpvNative
{
    const string Lib = "libmpv";

    // Events (mpv_event_id) and formats (mpv_format) used here.
    public const int EventShutdown = 1, EventLogMessage = 2, EventEndFile = 7, EventFileLoaded = 8, EventVideoReconfig = 17,
        EventPlaybackRestart = 21, EventPropertyChange = 22;
    public const int FormatFlag = 3, FormatInt64 = 4, FormatDouble = 5;
    public const int EndFileError = 4;

    // Render parameters (mpv_render_param_type).
    public const int RenderParamInvalid = 0, RenderParamApiType = 1, RenderParamOpenGlInitParams = 2, RenderParamOpenGlFbo = 3,
        RenderParamFlipY = 4, RenderParamSwSize = 17, RenderParamSwFormat = 18, RenderParamSwStride = 19, RenderParamSwPointer = 20;

    /// <summary>mpv_render_context_update flag: a new frame should be drawn.</summary>
    public const ulong RenderUpdateFrame = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Event
    {
        public int EventId;
        public int Error;
        public ulong ReplyUserdata;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EventProperty
    {
        public IntPtr Name;
        public int Format;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct EndFileData
    {
        public int Reason;
        public int Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct LogMessageData
    {
        public IntPtr Prefix;
        public IntPtr Level;
        public IntPtr Text;
        public int LogLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RenderParam
    {
        public int Type;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OpenGlInitParams
    {
        public IntPtr GetProcAddress;
        public IntPtr GetProcAddressCtx;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct OpenGlFbo
    {
        public int Fbo;
        public int W;
        public int H;
        public int InternalFormat;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr GetProcAddressFn(IntPtr ctx, IntPtr name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void UpdateFn(IntPtr ctx);

    static MpvNative() => NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, Resolve);

    static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib)
            return IntPtr.Zero;
        string[] candidates = OperatingSystem.IsWindows()
            ? [Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"), "libmpv-2.dll", "mpv-2.dll"]
            : ["libmpv.so.2", "libmpv.so"];
        foreach (var candidate in candidates)
            if (NativeLibrary.TryLoad(candidate, assembly, path, out var handle))
                return handle;
        return IntPtr.Zero;
    }

    /// <summary>Whether libmpv can be loaded on this PC.</summary>
    public static bool IsAvailable()
    {
        try
        {
            return mpv_client_api_version() >> 16 >= 2;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern ulong mpv_client_api_version();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_create();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int mpv_initialize(IntPtr handle);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_terminate_destroy(IntPtr handle);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_error_string(int error);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_option_string(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_set_property_string(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_get_property(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out double data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
    public static extern int mpv_get_property_long(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format, out long data);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_observe_property(IntPtr handle, ulong userdata, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_request_log_messages(IntPtr handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string minLevel);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr mpv_wait_event(IntPtr handle, double timeout);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_wakeup(IntPtr handle);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] static extern int mpv_command(IntPtr handle, IntPtr args);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_render_context_create(out IntPtr context, IntPtr handle, RenderParam[] parameters);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mpv_render_context_render(IntPtr context, RenderParam[] parameters);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mpv_render_context_set_update_callback(IntPtr context, UpdateFn? callback, IntPtr ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern ulong mpv_render_context_update(IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_render_context_report_swap(IntPtr context);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void mpv_render_context_free(IntPtr context);

    public static string ErrorText(int error) => Marshal.PtrToStringUTF8(mpv_error_string(error)) ?? error.ToString();

    /// <summary>Runs an mpv command (e.g. "loadfile", url) with UTF-8 arguments; returns mpv's error code (0 = ok).</summary>
    public static int Command(IntPtr handle, params string[] args)
    {
        var strings = args.Select(Marshal.StringToCoTaskMemUTF8).ToArray();
        var argv = Marshal.AllocHGlobal(IntPtr.Size * (strings.Length + 1));
        try
        {
            for (var i = 0; i < strings.Length; i++)
                Marshal.WriteIntPtr(argv, i * IntPtr.Size, strings[i]);
            Marshal.WriteIntPtr(argv, strings.Length * IntPtr.Size, IntPtr.Zero);
            return mpv_command(handle, argv);
        }
        finally
        {
            Marshal.FreeHGlobal(argv);
            foreach (var s in strings)
                Marshal.FreeCoTaskMem(s);
        }
    }
}
