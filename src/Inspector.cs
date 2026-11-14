using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace PopupGuard
{
    public class Inspector
    {
        readonly Dictionary<string,List<ProcessInfo>> chains=new Dictionary<string,List<ProcessInfo>>();
        Dictionary<int,int> parents=new Dictionary<int,int>(); DateTime snapshotAt=DateTime.MinValue;
        public static ProcessInfo ReadProcess(int pid)
        {
            var result=new ProcessInfo { Pid=pid };
            IntPtr handle=Native.OpenProcess(0x1000,false,(uint)pid);
            if(handle==IntPtr.Zero) return result;
            try
            {
                long c,e,k,u; if(Native.GetProcessTimes(handle,out c,out e,out k,out u)) result.Created=c;
                var b=new StringBuilder(32768); int count=b.Capacity;
                if(Native.QueryFullProcessImageName(handle,0,b,ref count)) { result.Path=b.ToString(); result.Name=System.IO.Path.GetFileName(result.Path); }
            } finally { Native.CloseHandle(handle); }
            return result;
        }
        public void Snapshot(bool force)
        {
            if(!force && (DateTime.UtcNow-snapshotAt).TotalSeconds<3) return;
            var next=new Dictionary<int,int>(); IntPtr snapshot=Native.CreateToolhelp32Snapshot(2,0);
            if(snapshot==new IntPtr(-1)) return;
            try
            {
                var entry=new Native.ProcessEntry { Size=(uint)Marshal.SizeOf(typeof(Native.ProcessEntry)) };
                if(Native.Process32FirstW(snapshot,ref entry)) do { next[(int)entry.Pid]=(int)entry.ParentPid; } while(Native.Process32NextW(snapshot,ref entry));
            } finally { Native.CloseHandle(snapshot); }
            parents=next; snapshotAt=DateTime.UtcNow;
            if(chains.Count>1024) chains.Clear();
        }
        public List<ProcessInfo> Trace(ProcessInfo leaf)
        {
            List<ProcessInfo> cached; if(chains.TryGetValue(leaf.Key,out cached)) return cached;
            Snapshot(true); var list=new List<ProcessInfo> { leaf }; var seen=new HashSet<int> { leaf.Pid }; ProcessInfo child=leaf;
            for(int i=0;i<12;i++)
            {
                if(!Safety.SameInstance(child,ReadProcess(child.Pid))) break;
                int parentId; if(!parents.TryGetValue(child.Pid,out parentId)||parentId==0||!seen.Add(parentId)) break;
                child.ParentPid=parentId;
                var parent=ReadProcess(parentId);
                // A live PID is insufficient: creation time must precede the child.
                if(parent.Created==0 || child.Created==0 || parent.Created>child.Created) break;
                if(!Safety.SameInstance(child,ReadProcess(child.Pid))) break;
                list.Add(parent); child=parent;
            }
            // Cache only a chain verified while both endpoints existed; never invent a missing parent.
            chains[leaf.Key]=list; return list;
        }
        public WindowInfo ReadWindow(IntPtr handle, bool includeHidden)
        {
            if(!Native.IsWindow(handle) || (!includeHidden && (!Native.IsWindowVisible(handle)||Native.IsIconic(handle)))) return null;
            if(Native.GetAncestor(handle,2)!=handle) return null;
            uint pid; Native.GetWindowThreadProcessId(handle,out pid); if(pid==0) return null;
            Native.Rect r; if(!Native.GetWindowRect(handle,out r)||r.Right-r.Left<20||r.Bottom-r.Top<20) return null;
            int cloaked; if(Native.DwmGetWindowAttribute(handle,14,out cloaked,4)==0 && cloaked!=0) return null;
            var p=ReadProcess((int)pid);
            var w=new WindowInfo { Handle=handle,ObservedAt=DateTime.Now,Title=Native.Title(handle),Class=Native.Class(handle),Process=p,Bounds=r.Rectangle,
                Style=Native.GetWindowLongPtr(handle,-16).ToInt64(),ExStyle=Native.GetWindowLongPtr(handle,-20).ToInt64() };
            try { w.Dpi=Native.GetDpiForWindow(handle); } catch(EntryPointNotFoundException) { }
            Classifier.Score(w,Screen.FromRectangle(w.Bounds).WorkingArea); return w;
        }
        public bool StillSame(WindowInfo w)
        {
            if(!Native.IsWindow(w.Handle)) return false;
            uint pid; Native.GetWindowThreadProcessId(w.Handle,out pid);
            return pid==w.Process.Pid && Native.Class(w.Handle)==w.Class && Safety.SameInstance(w.Process,ReadProcess((int)pid));
        }
    }
}
