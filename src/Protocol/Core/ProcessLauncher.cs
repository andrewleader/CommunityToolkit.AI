using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CommunityToolkit.AI.Protocol;

/// <summary>
/// Starts a background process without inheriting any handles from the caller, so a long-lived broker
/// or host never keeps the app's console or redirected output pipes open.
/// </summary>
internal static class ProcessLauncher
{
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateSuspended = 0x00000004;

    public static Process Start(string exe, IEnumerable<string> args, string workingDirectory, bool breakawayFromJob, Action<IntPtr>? beforeResume = null)
    {
        var commandLine = new StringBuilder(Quote(exe));
        foreach (var arg in args)
        {
            commandLine.Append(' ').Append(Quote(arg));
        }

        var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        // Always start suspended so the Process object can open its handle before the child can exit.
        uint flags = CreateNoWindow | CreateUnicodeEnvironment | CreateSuspended;
        if (!CreateProcessW(exe, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags | (breakawayFromJob ? CreateBreakawayFromJob : 0), IntPtr.Zero, workingDirectory, ref si, out var pi))
        {
            // Breakaway is refused when the caller's job does not allow it; fall back to staying in the job.
            if (!breakawayFromJob || !CreateProcessW(exe, commandLine, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, workingDirectory, ref si, out pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start {exe}.");
            }
        }

        try
        {
            var process = Process.GetProcessById(pi.dwProcessId);
            _ = process.Handle;
            beforeResume?.Invoke(pi.hProcess);
            ResumeThread(pi.hThread);
            return process;
        }
        catch
        {
            TerminateProcess(pi.hProcess, 1);
            throw;
        }
        finally
        {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
    }

    // Windows command-line quoting rules for CommandLineToArgvW.
    private static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return arg;
        }

        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
