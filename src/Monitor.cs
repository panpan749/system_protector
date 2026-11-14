using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace PopupGuard
{
    public class HiddenWindow
    {
        public long Handle; public ProcessInfo Process; public string Title,Class; public DateTime HiddenAt;
        public WindowInfo Window() { return new WindowInfo { Handle=new IntPtr(Handle),Process=Process,Title=Title,Class=Class }; }
    }
    public class Activity
    {
        public string Id=Guid.NewGuid().ToString("N"); public DateTime Time=DateTime.Now; public string Action,Reason; public WindowInfo Window;
        public string ScreenshotFile="",ScreenshotStatus="",ScreenshotMethod=""; public List<ProcessInfo> Chain=new List<ProcessInfo>();
        public DateTime ScreenshotTime;
        public bool EvidenceReady;
    }
    public sealed class Monitor : IDisposable
    {
        public readonly Inspector Inspector=new Inspector(); public readonly Store Store; public readonly EvidenceStore Evidence;
        public readonly PresetCatalog Presets=new PresetCatalog();
        public readonly List<Activity> History=new List<Activity>(); public readonly List<HiddenWindow> Hidden=new List<HiddenWindow>();
        public event Action<WindowInfo> Suspicious; public event Action Changed;
        public bool Paused; public bool Busy; public bool HookAvailable; public int BlockCount;
        readonly Timer timer=new Timer { Interval=50 }; readonly Control dispatcher=new Control();
        readonly HashSet<IntPtr> active=new HashSet<IntPtr>(); bool drainQueued;
        readonly int processFilter;
        readonly Native.EventProc callback; readonly GCHandle callbackRoot; readonly List<IntPtr> hooks=new List<IntPtr>();
        readonly HashSet<IntPtr> pending=new HashSet<IntPtr>();
        readonly HashSet<IntPtr> shown=new HashSet<IntPtr>();
        readonly Dictionary<string,DateTime> seen=new Dictionary<string,DateTime>();
        readonly HashSet<string> captureAfterUnlock=new HashSet<string>();
        readonly Dictionary<string,DateTime> dismissed=new Dictionary<string,DateTime>();
        readonly Dictionary<string,DateTime> bypass=new Dictionary<string,DateTime>();
        readonly Dictionary<string,Tuple<long,long,string>> hashes=new Dictionary<string,Tuple<long,long,string>>(StringComparer.OrdinalIgnoreCase);
        DateTime lastScan=DateTime.MinValue,lastPrompt=DateTime.UtcNow.AddSeconds(-45); bool disposed;
        public Monitor(Store store,int processFilter=0)
        {
            this.processFilter=processFilter;
            var dispatchHandle=dispatcher.Handle;
            Store=store; Evidence=new EvidenceStore(store); History.AddRange(Evidence.Load()); Evidence.PruneImages(); Evidence.Save(History);
            if(Store.Settings.CaptureScreenshots) CaptureBroker.WarmUp();
            Recover(); callback=OnEvent; callbackRoot=GCHandle.Alloc(callback);
            hooks.Add(Native.SetWinEventHook(0x8002,0x8002,IntPtr.Zero,callback,0,0,2));
            hooks.Add(Native.SetWinEventHook(0x800C,0x800C,IntPtr.Zero,callback,0,0,2));
            HookAvailable=hooks.All(h=>h!=IntPtr.Zero); timer.Tick+=Tick; timer.Start();
        }
        void OnEvent(IntPtr hook,uint ev,IntPtr hwnd,int obj,int child,uint thread,uint time)
        { if(obj==0 && child==0 && hwnd!=IntPtr.Zero && pending.Count<2048) { pending.Add(hwnd); if(ev==0x8002&&shown.Count<2048) shown.Add(hwnd); QueueDrain(); } }
        void QueueDrain()
        {
            if(disposed||drainQueued) return; drainQueued=true;
            try { dispatcher.BeginInvoke(new Action(()=>{ drainQueued=false; if(!disposed) Drain(); })); }
            catch(InvalidOperationException) { drainQueued=false; }
        }
        public Activity Record(string action,WindowInfo w,string reason)
        { return RecordCore(action,w,reason,true); }
        Activity RecordCore(string action,WindowInfo w,string reason,bool captureNow)
        {
            var a=new Activity { Action=action,Window=EvidenceStore.Copy(w),Reason=reason,EvidenceReady=w!=null&&w.Hash.Length==64 };
            if(w!=null&&w.ObservedAt!=DateTime.MinValue&&(action=="待确认"||action=="手动定位"||action=="准备隐藏")) a.Time=w.ObservedAt;
            if(w!=null)
            {
                a.Chain=Inspector.Trace(w.Process).ToList();
                var prior=History.FirstOrDefault(x=>x.Window!=null&&x.Window.Key==w.Key&&!string.IsNullOrEmpty(x.ScreenshotFile));
                if(prior!=null&&action!="待确认"&&action!="手动定位"&&action!="准备隐藏")
                { a.ScreenshotFile=prior.ScreenshotFile; a.ScreenshotStatus=prior.ScreenshotStatus; a.ScreenshotMethod=prior.ScreenshotMethod; a.ScreenshotTime=prior.ScreenshotTime; }
                else if(captureNow) { var capture=Evidence.Capture(w,Inspector); a.ScreenshotFile=capture.File; a.ScreenshotStatus=capture.Status; a.ScreenshotMethod=capture.Method; a.ScreenshotTime=capture.CapturedAt; }
                else a.ScreenshotStatus=EvidenceStore.CapturingStatus;
                if(a.ScreenshotStatus==EvidenceStore.DesktopUnavailableStatus&&captureAfterUnlock.Count<4096) captureAfterUnlock.Add(w.Key);
            }
            History.Insert(0,a); Evidence.Save(History);
            Store.Log(action,w,reason); if(Changed!=null) Changed(); return a;
        }
        public async Task<Activity> ObserveAsync(WindowInfo w,string action,string reason,Task<CaptureResult> earlyCapture=null)
        {
            // Start acquisition before ancestry, JSON writes, hashing, or UI refreshes.
            var captureTask=earlyCapture??Evidence.CaptureAsync(EvidenceStore.Copy(w),Inspector);
            var a=RecordCore(action,w,reason,false);
            if(a.ScreenshotStatus==EvidenceStore.CapturingStatus) {
                var capture=await captureTask;
                if(disposed||!History.Contains(a)) { Evidence.DiscardImage(capture.File); a.ScreenshotStatus="这次采集已取消"; return a; }
                a.ScreenshotFile=capture.File; a.ScreenshotStatus=capture.Status; a.ScreenshotMethod=capture.Method; a.ScreenshotTime=capture.CapturedAt;
                if(capture.Status==EvidenceStore.DesktopUnavailableStatus&&captureAfterUnlock.Count<4096) captureAfterUnlock.Add(w.Key);
                Evidence.Save(History); if(Changed!=null) Changed();
            }
            else { var unused=await captureTask; Evidence.DiscardImage(unused.File); }
            if(a.EvidenceReady) return a;
            try
            {
                var before=new FileInfo(w.Process.Path); long size=before.Length,stamp=before.LastWriteTimeUtc.Ticks;
                string hash=await Task.Run(()=>Safety.HashFile(w.Process.Path)); before.Refresh();
                if(before.Exists&&before.Length==size&&before.LastWriteTimeUtc.Ticks==stamp&&hash.Length==64)
                { a.Window.Hash=hash; a.EvidenceReady=true; w.Hash=hash; }
            } catch { }
            Evidence.Save(History); if(Changed!=null&&!disposed) Changed(); return a;
        }
        public Rule ConfirmRule(WindowInfo w,bool allow,bool anyTitle,bool historical)
        {
            if(Safety.ProtectedReason(w.Process)!="") throw new InvalidOperationException("该来源受保护，不能创建自动拦截规则。");
            if(!historical&&!Inspector.StillSame(w)) throw new InvalidOperationException("窗口已消失，请从历史记录核对后添加规则。");
            string fresh=Safety.HashFile(w.Process.Path);
            if(w.Hash.Length!=64||fresh==""||fresh!=w.Hash) throw new InvalidOperationException("文件已变化、缺失或未保存原始指纹，请在下次出现时重新核对。历史截图仍可查看。");
            var rule=Rule.From(w,allow,anyTitle); var previous=Store.Settings.Rules.ToList();
            Store.Settings.Rules.RemoveAll(r=>string.Equals(r.Path,rule.Path,StringComparison.OrdinalIgnoreCase)&&r.Hash==rule.Hash&&r.WindowClass==rule.WindowClass&&r.Title==rule.Title&&r.AnyTitle==rule.AnyTitle);
            Store.Settings.Rules.Add(rule);
            try { Store.Save(); } catch { Store.Settings.Rules=previous; throw; }
            Record(allow?"已加入白名单":"已加入黑名单",w,(historical?"从历史确认；":"")+(anyTitle?"同类窗口所有标题":"精确标题"));
            if(!allow&&Inspector.StillSame(w)) Hide(w,"用户确认广告"); Rescan(); return rule;
        }
        public void ClearHistory() { Evidence.Clear(History); if(Changed!=null) Changed(); }
        string Journal { get { return Path.Combine(Store.DirectoryPath,"hidden-windows.json"); } }
        void SaveJournal()
        {
            string temp=Journal+".tmp"; File.WriteAllText(temp,new JavaScriptSerializer().Serialize(Hidden));
            if(File.Exists(Journal)) File.Replace(temp,Journal,null); else File.Move(temp,Journal);
        }
        void Recover()
        {
            if(!File.Exists(Journal)) return;
            try
            {
                var old=new JavaScriptSerializer().Deserialize<List<HiddenWindow>>(File.ReadAllText(Journal));
                if(old!=null) Hidden.AddRange(old.Where(x=>x!=null && x.Process!=null && x.Class!=null && x.Title!=null));
                RestoreAll();
            } catch(Exception e) { Store.Warning="隐藏窗口恢复记录不可读取："+e.Message; }
        }
        void Tick(object sender,EventArgs e)
        {
            if(disposed) return;
            try
            {
                if((DateTime.UtcNow-lastScan).TotalSeconds>=2)
                {
                    // Re-observe surviving windows after unlock using a NEW timestamp; old missing images stay missing.
                    RetryAfterUnlock();
                    foreach(var h in Native.Windows()) if(pending.Count<2048) pending.Add(h);
                    lastScan=DateTime.UtcNow;
                    if(seen.Count>4096) seen.Clear();
                    foreach(var key in dismissed.Where(x=>x.Value<DateTime.UtcNow).Select(x=>x.Key).ToList()) dismissed.Remove(key);
                    foreach(var key in bypass.Where(x=>x.Value<DateTime.UtcNow).Select(x=>x.Key).ToList()) bypass.Remove(key);
                    if(hashes.Count>256) hashes.Clear();
                    if(Hidden.RemoveAll(x=>!Inspector.StillSame(x.Window()))>0) { SaveJournal(); if(Changed!=null) Changed(); }
                }
                Drain();
            } catch(Exception ex) { Store.Log("检测异常",null,ex.Message); }
        }
        void Drain()
        {
            if(Paused||disposed) return;
            int inspected=0;
            while(active.Count<16&&pending.Count>0&&inspected++<40) {
                IntPtr h=shown.FirstOrDefault(x=>pending.Contains(x)&&!active.Contains(x));
                if(h==IntPtr.Zero) h=pending.FirstOrDefault(x=>!active.Contains(x));
                if(h==IntPtr.Zero) break;
                pending.Remove(h); bool newShow=shown.Remove(h); active.Add(h); ProcessWindow(h,newShow);
            }
        }
        static bool Scope(Rule r,WindowInfo w) { return r.Enabled&&string.Equals(r.Path,w.Process.Path,StringComparison.OrdinalIgnoreCase)&&r.WindowClass==w.Class&&(r.AnyTitle||r.Title==w.Title); }
        async void ProcessWindow(IntPtr h,bool newShow)
        {
            Task<CaptureResult> early=null; bool consumed=false;
            try {
                    uint pid; Native.GetWindowThreadProcessId(h,out pid); if(processFilter!=0&&pid!=processFilter) return;
                    WindowInfo w=Inspector.ReadWindow(h,false); if(w==null || Safety.ProtectedReason(w.Process)!="") return;
                    DateTime until; if(bypass.TryGetValue(w.Process.Key,out until)&&until>DateTime.UtcNow) return;
                    bool observe=!seen.ContainsKey(w.Key)||(newShow&&!captureAfterUnlock.Contains(w.Key)&&(DateTime.UtcNow-seen[w.Key]).TotalMilliseconds>=500);
                    bool potential=Store.Settings.Rules.Any(r=>!r.Allow&&Scope(r,w))||Presets.Match(w,Store.Settings)!=null||(observe&&w.Score>=55);
                    if(potential&&!Store.Settings.Rules.Any(r=>r.Allow&&Scope(r,w))) early=Evidence.CaptureAsync(EvidenceStore.Copy(w),Inspector);
                    // Give other show events a chance to dispatch captures before metadata work.
                    await Task.Yield(); if(Paused||disposed) return;
                    bool candidate=Store.Settings.Rules.Any(r=>r.Enabled&&string.Equals(r.Path,w.Process.Path,StringComparison.OrdinalIgnoreCase));
                    if(candidate)
                    {
                        w.Hash=await GetHash(w.Process.Path);
                        if(disposed||Paused) return;
                        if(Store.Settings.Rules.Any(r=>r.Allow&&r.Matches(w))) return;
                        Rule rule=Store.Settings.Rules.FirstOrDefault(r=>!r.Allow&&r.Matches(w));
                        if(rule!=null) { consumed=true; await HideAsync(w,"命中已确认规则 "+rule.Id.Substring(0,8),rule,null,early); return; }
                    }
                    var preset=Presets.Match(w,Store.Settings);
                    if(preset!=null)
                    {
                        if(w.Hash.Length!=64) w.Hash=await GetHash(w.Process.Path);
                        if(disposed||Paused) return;
                        // Re-read after asynchronous work: the same HWND can change from an ad to a normal UI.
                        var fresh=Inspector.ReadWindow(h,false);
                        if(w.Hash.Length==64&&fresh!=null&&Safety.SameInstance(w.Process,fresh.Process)&&w.Class==fresh.Class&&w.Title==fresh.Title)
                        {
                            fresh.Hash=w.Hash;
                            if(Presets.Enabled(preset,Store.Settings)&&preset.Matches(fresh)&&!Store.Settings.Rules.Any(r=>r.Allow&&r.Matches(fresh)))
                            { consumed=true; await HideAsync(fresh,"预置广告规则："+preset.Name+" ["+preset.Id+"]",null,preset,early); return; }
                        }
                    }
                    if(w.Score<55) return;
                    if(observe)
                    {
                        seen[w.Key]=DateTime.UtcNow; consumed=true;
                        await ObserveAsync(w,"待确认",w.Reason+"（特征分 "+w.Score+"，不是恶意判定）",early);
                        if(disposed||Paused) return;
                    }
                    string source=w.Process.Path+"|"+w.Class;
                    if(Store.Settings.AutoPrompt&&!Busy&&Inspector.StillSame(w)&&Native.IsWindowVisible(w.Handle)&&(DateTime.UtcNow-lastPrompt).TotalSeconds>60&&!dismissed.ContainsKey(source)&&EvidenceStore.DesktopAvailable())
                    {
                        lastPrompt=DateTime.UtcNow; dismissed[source]=DateTime.UtcNow.AddMinutes(15);
                        if(Suspicious!=null) Suspicious(w);
                    }
            }
            catch(Exception ex) { Store.Log("检测异常",null,ex.Message); }
            finally {
                if(early!=null&&!consumed) DiscardLater(early);
                active.Remove(h); if(!disposed&&pending.Any(x=>!active.Contains(x))) QueueDrain();
            }
        }
        async void DiscardLater(Task<CaptureResult> task) { try { var result=await task; Evidence.DiscardImage(result.File); } catch { } }
        async Task<string> GetHash(string path)
        {
            try
            {
                var f=new FileInfo(path); Tuple<long,long,string> old;
                if(hashes.TryGetValue(path,out old)&&old.Item1==f.Length&&old.Item2==f.LastWriteTimeUtc.Ticks) return old.Item3;
                long size=f.Length,stamp=f.LastWriteTimeUtc.Ticks; string hash=await Task.Run(()=>Safety.HashFile(path)); f.Refresh();
                if(!f.Exists || f.Length!=size || f.LastWriteTimeUtc.Ticks!=stamp) return "";
                hashes[path]=Tuple.Create(size,stamp,hash); return hash;
            } catch { return ""; }
        }
        public bool Hide(WindowInfo w,string reason)
        { return HideCore(w,reason,false); }
        async Task<bool> HideAsync(WindowInfo w,string reason,Rule rule,PresetRule preset,Task<CaptureResult> earlyCapture=null)
        {
            await ObserveAsync(w,"准备隐藏",reason,earlyCapture);
            if(disposed||Paused) return false;
            var fresh=Inspector.ReadWindow(w.Handle,false);
            if(fresh==null||fresh.Class!=w.Class||fresh.Title!=w.Title||!Safety.SameInstance(fresh.Process,w.Process)) return false;
            fresh.Hash=await GetHash(fresh.Process.Path);
            if(disposed||Paused||fresh.Hash!=w.Hash||fresh.Hash.Length!=64||Store.Settings.Rules.Any(r=>r.Allow&&r.Matches(fresh))) return false;
            bool stillMatched=rule!=null?Store.Settings.Rules.Contains(rule)&&rule.Enabled&&!rule.Allow&&rule.Matches(fresh):preset!=null&&Presets.Enabled(preset,Store.Settings)&&preset.Matches(fresh);
            return stillMatched&&HideCore(fresh,reason,true);
        }
        bool HideCore(WindowInfo w,string reason,bool captured)
        {
            if(Safety.ProtectedReason(w.Process)!="" || !Inspector.StillSame(w)||Native.Title(w.Handle)!=w.Title) return false;
            if(Hidden.Count>=500) { Store.Warning="隐藏窗口已达上限，请先恢复或退出来源程序。"; return false; }
            var existing=Hidden.FirstOrDefault(x=>x.Handle==w.Handle.ToInt64()&&Safety.SameInstance(x.Process,w.Process));
            var item=existing??new HiddenWindow { Handle=w.Handle.ToInt64(),Process=w.Process,Title=w.Title,Class=w.Class,HiddenAt=DateTime.Now };
            if(existing==null) Hidden.Add(item);
            // Capture before hiding. The stored identity remains usable after the process exits.
            if(!captured) Record("准备隐藏",w,reason);
            try { SaveJournal(); } catch(Exception ex) { if(existing==null) Hidden.Remove(item); Record("未拦截",w,"无法保存恢复记录："+ex.Message); return false; }
            if(!Native.ShowWindowAsync(w.Handle,0)) { if(existing==null) Hidden.Remove(item); SaveJournal(); Record("隐藏请求失败",w,"可能受权限限制"); return false; }
            BlockCount++; Record("已请求隐藏",w,reason); return true;
        }
        public void RestoreAll()
        {
            foreach(var item in Hidden.ToList())
            {
                var w=item.Window();
                if(Inspector.StillSame(w))
                {
                    bypass[w.Process.Key]=DateTime.UtcNow.AddMinutes(5);
                    if(!Native.ShowWindowAsync(w.Handle,4)) continue;
                    Store.Log("已请求恢复",w,"五分钟内暂不再次隐藏此进程");
                }
                Hidden.Remove(item);
            }
            SaveJournal(); if(Changed!=null) Changed();
        }
        public void Dismiss(WindowInfo w) { if(w!=null) dismissed[w.Process.Path+"|"+w.Class]=DateTime.UtcNow.AddMinutes(15); }
        void RetryAfterUnlock()
        {
            if(captureAfterUnlock.Count==0||!EvidenceStore.DesktopAvailable()) return;
            foreach(string key in captureAfterUnlock) seen.Remove(key);
            captureAfterUnlock.Clear();
        }
        public void Rescan() { seen.Clear(); hashes.Clear(); lastScan=DateTime.MinValue; }
        public void Dispose()
        {
            disposed=true; Paused=true; timer.Stop(); timer.Dispose(); foreach(var h in hooks) if(h!=IntPtr.Zero) Native.UnhookWinEvent(h);
            if(callbackRoot.IsAllocated) callbackRoot.Free(); try { RestoreAll(); } catch(Exception e) { Store.Log("恢复失败",null,e.Message); }
            dispatcher.Dispose();
        }
    }
}
