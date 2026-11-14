using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace PopupGuard
{
    public static class Native
    {
        public delegate bool EnumProc(IntPtr hwnd, IntPtr param);
        public delegate void EventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; public Rectangle Rectangle { get { return Rectangle.FromLTRB(Left, Top, Right, Bottom); } } }
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; public Point(int x, int y) { X=x; Y=y; } }
        [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct ProcessEntry
        {
            public uint Size, Usage, Pid; public UIntPtr Heap; public uint Module, Threads, ParentPid; public int Priority; public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst=260)] public string Name;
        }
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] public struct SessionInfo
        {
            public uint SessionId; public int SessionState,SessionFlags;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=33)] public string Station;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=21)] public string User;
            [MarshalAs(UnmanagedType.ByValTStr,SizeConst=18)] public string Domain;
            public long Logon,Connect,Disconnect,LastInput,Current;
            public uint IncomingBytes,OutgoingBytes,IncomingFrames,OutgoingFrames,IncomingCompressedBytes,OutgoingCompressedBytes;
        }
        [StructLayout(LayoutKind.Sequential)] public struct SessionInfoEx { public uint Level; public SessionInfo Data; }
        [DllImport("wtsapi32.dll",CharSet=CharSet.Unicode)] public static extern bool WTSQuerySessionInformationW(IntPtr server,int session,int infoClass,out IntPtr buffer,out int bytes);
        [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr memory);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback, IntPtr param);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flag);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hwnd, int command);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, EventProc callback, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string className,string title);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
        [DllImport("user32.dll")] public static extern bool CloseDesktop(IntPtr desktop);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder data,int length,out int needed);
        [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        public static List<IntPtr> Windows()
        {
            var result=new List<IntPtr>(); EnumWindows(delegate(IntPtr h, IntPtr p) { result.Add(h); return true; }, IntPtr.Zero); return result;
        }
        public static string Title(IntPtr h) { var s=new StringBuilder(1024); GetWindowText(h,s,s.Capacity); return s.ToString(); }
        public static string Class(IntPtr h) { var s=new StringBuilder(256); GetClassName(h,s,s.Capacity); return s.ToString(); }
    }
}
