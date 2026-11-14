using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using PopupGuard;

public static class LatencyTests
{
    static int passed,failed; static string root; static readonly List<double> delays=new List<double>();
    static void Check(string label,bool okay) { Console.WriteLine((okay?"PASS ":"FAIL ")+label); if(okay) passed++; else failed++; }
    static void Pump(int ms) { var timer=Stopwatch.StartNew(); while(timer.ElapsedMilliseconds<ms) { Application.DoEvents(); Thread.Sleep(5); } }
    static void Wait(Func<bool> condition,int timeout=6000) { var timer=Stopwatch.StartNew(); while(!condition()&&timer.ElapsedMilliseconds<timeout) Pump(10); }
    static bool Content(Image image) {
        if(image==null) return false; using(var bitmap=new Bitmap(image)) {
            int blue=0,green=0,white=0,total=0;
            for(int y=0;y<bitmap.Height;y+=3) for(int x=0;x<bitmap.Width;x+=3) {
                var c=bitmap.GetPixel(x,y); total++;
                if(Math.Abs(c.R-22)<8&&Math.Abs(c.G-45)<8&&Math.Abs(c.B-63)<8) blue++;
                if(c.G>240&&c.R<15&&c.B<15) green++; if(c.R>230&&c.G>230&&c.B>230) white++;
            }
            return blue>total*.5&&green>total*.025&&white>total*.003;
        }
    }
    static Process Start(string mode) { return Process.Start(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"PopupGuard.GpuFixture.exe"),mode); }
    static List<IntPtr> Windows(Process p) {
        return Native.Windows().Where(h=>{ uint pid; Native.GetWindowThreadProcessId(h,out pid); return pid==p.Id&&Native.Class(h)=="PopupGuard.GpuFixture"; }).ToList();
    }
    static void Ready(Process p,int count) { Wait(()=>!p.HasExited&&Windows(p).Count==count&&Windows(p).Any(h=>Native.Title(h).Contains("READY"))); if(p.HasExited) throw new Exception("Fixture exited early"); }
    static void Stop(Process p) { if(!p.HasExited) foreach(var h in Windows(p)) Native.PostMessage(h,0x10,IntPtr.Zero,IntPtr.Zero); p.WaitForExit(3000); p.Dispose(); }
    static void Brief(int number,string mode,int count) {
        var process=Start(mode);
        try {
            Ready(process,count);
            var store=new Store(Path.Combine(root,"brief-"+number)); store.Settings.AutoPrompt=false;
            using(var monitor=new PopupGuard.Monitor(store,process.Id)) {
                var handles=Windows(process); var start=DateTime.Now;
                Native.PostMessage(handles.First(h=>Native.Title(h).Contains("READY")),0x8002,IntPtr.Zero,IntPtr.Zero);
                Wait(()=>process.HasExited,3000); var exit=DateTime.Now;
                Wait(()=>monitor.History.Count(a=>a.Window.Process.Pid==process.Id&&a.ScreenshotStatus!=EvidenceStore.CapturingStatus&&a.EvidenceReady)>=count);
                var records=monitor.History.Where(a=>a.Window.Process.Pid==process.Id&&a.Action=="待确认").ToList();
                var good=records.Where(a=>a.ScreenshotFile!=""&&a.ScreenshotTime>=start&&a.ScreenshotTime<exit).ToList();
                Check(mode+" captured every short-lived window before exit",process.HasExited&&good.Count==count);
                foreach(var a in good) {
                    using(var image=monitor.Evidence.LoadImage(a.ScreenshotFile)) Check("brief snapshot contains rendered text and marker",Content(image));
                    double ms=(a.ScreenshotTime-start).TotalMilliseconds; delays.Add(ms);
                    Console.WriteLine("LATENCY_MS "+ms.ToString("F1",System.Globalization.CultureInfo.InvariantCulture));
                    Check("history keeps image and acquisition timestamp after process exits",monitor.Evidence.Load().Any(x=>x.Id==a.Id&&Math.Abs((x.ScreenshotTime-a.ScreenshotTime).TotalMilliseconds)<2));
                }
            }
        } finally { Stop(process); }
    }
    [STAThread] public static int Main() {
        Application.EnableVisualStyles(); root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data","latency-"+DateTime.Now.ToString("yyyyMMddHHmmss")); Directory.CreateDirectory(root);
        Console.WriteLine("ARTIFACTS "+root);
        try {
            if(!EvidenceStore.DesktopAvailable()) throw new Exception("Unlocked real desktop required.");
            var warm=CaptureBroker.WarmUpAsync(); Wait(()=>warm.IsCompleted); Check("four capture workers prewarmed before popup shows",warm.IsCompleted&&warm.Result);
            for(int i=0;i<6;i++) Brief(i,"--armed-500",1);
            var slow=Start("--blank");
            try {
                Wait(()=>!slow.HasExited&&Windows(slow).Any(Native.IsWindowVisible));
                var info=new Inspector().ReadWindow(Windows(slow).First(),false);
                var store=new EvidenceStore(new Store(Path.Combine(root,"slow"))); var pending=store.CaptureAsync(info,new Inspector()); Pump(60);
                Brief(6,"--burst",3);
                Wait(()=>pending.IsCompleted); Check("blank source fails without holding other windows behind it",pending.IsCompleted&&pending.Result.File=="");
            } finally { Stop(slow); }
            Check("all nine half-second popups captured",delays.Count==9);
            if(delays.Count>0) { Console.WriteLine("LATENCY median="+delays.OrderBy(x=>x).ElementAt(delays.Count/2).ToString("F1")+" max="+delays.Max().ToString("F1")+" ms"); Check("acquisition occurs within popup's 500 ms lifetime",delays.Max()<500); }
            File.WriteAllLines(Path.Combine(root,"latency-ms.txt"),delays.Select(x=>x.ToString("F1",System.Globalization.CultureInfo.InvariantCulture)));
        } catch(Exception e) { Check("integration exception: "+e,false); }
        finally { CaptureBroker.Shutdown(); }
        Console.WriteLine("RESULT "+passed+" passed / "+failed+" failed"); return failed==0?0:1;
    }
}
