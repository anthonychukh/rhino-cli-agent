using System.Runtime.InteropServices;
using Rhino;

namespace RhinoAgent.Attachments;

internal sealed class AgentCommandLineAttachmentPasteHook : IDisposable
{
    private const int WhKeyboardLowLevel = 13;
    private const int HcAction = 0;
    private const uint LlkhfUp = 0x80;
    private const int VkControl = 0x11;
    private const int VkV = 0x56;

    private readonly AgentAttachmentComposer _composer;
    private readonly HookProcedure _procedure;
    private IntPtr _hook;
    private bool _vKeyDown;
    private bool _suppressVKeyUp;

    public static int LastInstallError { get; private set; }

    private AgentCommandLineAttachmentPasteHook(AgentAttachmentComposer composer)
    {
        _composer = composer;
        _procedure = HandleKeyboard;
        _hook = SetWindowsHookEx(
            WhKeyboardLowLevel,
            _procedure,
            GetModuleHandle(null),
            0);
        LastInstallError = _hook == IntPtr.Zero ? Marshal.GetLastPInvokeError() : 0;
    }

    public static AgentCommandLineAttachmentPasteHook? TryInstall(AgentAttachmentComposer composer)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var hook = new AgentCommandLineAttachmentPasteHook(composer);
        if (hook._hook != IntPtr.Zero)
            return hook;

        hook.Dispose();
        return null;
    }

    private IntPtr HandleKeyboard(int code, IntPtr message, IntPtr keyboardData)
    {
        if (code < HcAction)
            return CallNextHookEx(_hook, code, message, keyboardData);

        var key = Marshal.PtrToStructure<LowLevelKeyboardInput>(keyboardData);
        if (key.VirtualKey != VkV)
            return CallNextHookEx(_hook, code, message, keyboardData);

        if ((key.Flags & LlkhfUp) != 0)
        {
            var suppress = _suppressVKeyUp && IsRhinoForeground();
            _vKeyDown = false;
            _suppressVKeyUp = false;
            return suppress
                ? new IntPtr(1)
                : CallNextHookEx(_hook, code, message, keyboardData);
        }

        if (!IsRhinoForeground())
            return CallNextHookEx(_hook, code, message, keyboardData);

        if (_vKeyDown)
            return _suppressVKeyUp
                ? new IntPtr(1)
                : CallNextHookEx(_hook, code, message, keyboardData);

        _vKeyDown = true;

        if ((GetKeyState(VkControl) & 0x8000) == 0
            || !_composer.TryCaptureClipboard(out var insertion))
            return CallNextHookEx(_hook, code, message, keyboardData);

        _suppressVKeyUp = true;
        RhinoApp.SendKeystrokes(insertion, appendReturn: false);
        return new IntPtr(1);
    }

    private static bool IsRhinoForeground()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return false;

        GetWindowThreadProcessId(window, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero)
            return;

        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate IntPtr HookProcedure(int code, IntPtr virtualKey, IntPtr keyFlags);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LowLevelKeyboardInput
    {
        public readonly uint VirtualKey;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(
        int hookType,
        HookProcedure procedure,
        IntPtr module,
        uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(
        IntPtr hook,
        int code,
        IntPtr virtualKey,
        IntPtr keyFlags);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
