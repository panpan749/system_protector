using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace PopupGuard
{
    public sealed class NativeCaptureReply { public int Code=6; public long CapturedAt; }
    public static class CaptureBroker
    {
        static readonly object gate=new object();
        static readonly Dictionary<long,TaskCompletionSource<NativeCaptureReply>> pending=new Dictionary<long,TaskCompletionSource<NativeCaptureReply>>();
        static Process worker; static TaskCompletionSource<bool> ready; static Task reader; static long next;
        static CaptureBroker() { AppDomain.CurrentDomain.ProcessExit+=delegate { Shutdown(); }; }
        static TaskCompletionSource<T> Completion<T>() { return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously); }
        static string Encode(string text) {
            if(string.IsNullOrEmpty(text)) return "-";
            var result=new StringBuilder(text.Length*4); foreach(char c in text) result.Append(((int)c).ToString("X4",CultureInfo.InvariantCulture)); return result.ToString();
        }
        static void Reset(Process expected)
        {
            lock(gate) {
                if(expected!=worker) return;
                var old=worker; worker=null;
                if(ready!=null) ready.TrySetResult(false);
                foreach(var completion in pending.Values) completion.TrySetResult(new NativeCaptureReply { Code=8 }); pending.Clear();
                if(old!=null) { try { if(!old.HasExited) old.Kill(); } catch { } try { old.Dispose(); } catch { } }
            }
        }
        public static void Shutdown() { lock(gate) Reset(worker); }
        public static async void WarmUp() { try { await WarmUpAsync(); } catch { } }
        public static async Task<bool> WarmUpAsync()
        {
            Process process; Task<bool> signal;
            lock(gate) {
                if(worker==null) {
                    ready=Completion<bool>();
                    try {
                        string file=Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location),"PopupGuard.Capture.exe");
                        worker=Process.Start(new ProcessStartInfo(file,"--worker") { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardInput=true,RedirectStandardOutput=true,StandardOutputEncoding=Encoding.ASCII });
                        var owned=worker; var started=ready;
                        reader=Task.Factory.StartNew(()=>ReadReplies(owned,started),TaskCreationOptions.LongRunning);
                    } catch { worker=null; ready.TrySetResult(false); }
                }
                process=worker; signal=ready.Task;
            }
            if(await Task.WhenAny(signal,Task.Delay(6000)).ConfigureAwait(false)!=signal) { Reset(process); return false; }
            return await signal.ConfigureAwait(false);
        }
        static void ReadReplies(Process process,TaskCompletionSource<bool> started)
        {
            try {
                string line;
                while((line=process.StandardOutput.ReadLine())!=null) {
                    if(line.StartsWith("READY ",StringComparison.Ordinal)) { started.TrySetResult(line!="READY 0"); continue; }
                    var parts=line.Split(' '); long id,stamp; int code;
                    if(parts.Length!=3||!long.TryParse(parts[0],out id)||!int.TryParse(parts[1],out code)||!long.TryParse(parts[2],out stamp)) continue;
                    lock(gate) {
                        TaskCompletionSource<NativeCaptureReply> completion;
                        if(process==worker&&pending.TryGetValue(id,out completion)) { pending.Remove(id); completion.TrySetResult(new NativeCaptureReply { Code=code,CapturedAt=stamp }); }
                    }
                }
            } catch { }
            finally { started.TrySetResult(false); Reset(process); }
        }
        public static async Task<NativeCaptureReply> CaptureAsync(WindowInfo window,string output)
        {
            if(!await WarmUpAsync().ConfigureAwait(false)) return new NativeCaptureReply();
            Process process; TaskCompletionSource<NativeCaptureReply> completion=Completion<NativeCaptureReply>();
            lock(gate) {
                process=worker;
                if(process==null) return new NativeCaptureReply();
                if(pending.Count>=16) return new NativeCaptureReply { Code=7 };
                long id=++next; pending.Add(id,completion);
                try {
                    process.StandardInput.WriteLine(id+" "+window.Handle.ToInt64()+" "+window.Process.Pid+" "+window.Process.Created+" "+Encode(window.Title)+" "+Encode(window.Class)+" "+Encode(output));
                    process.StandardInput.Flush();
                } catch { Reset(process); }
            }
            if(await Task.WhenAny(completion.Task,Task.Delay(2600)).ConfigureAwait(false)!=completion.Task) { Reset(process); return new NativeCaptureReply { Code=8 }; }
            return await completion.Task.ConfigureAwait(false);
        }
    }
}
