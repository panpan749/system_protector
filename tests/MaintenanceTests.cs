using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;
using PopupGuard;

public static class MaintenanceTests
{
    static int passed,failed,skipped;
    static void Check(string name,bool okay) { Console.WriteLine((okay?"PASS ":"FAIL ")+name); if(okay) passed++; else failed++; }
    static void Reject(string name,Action action) { bool rejected=false; try { action(); } catch { rejected=true; } Check(name,rejected); }
    static SoftwareRule Custom(string id) { return new SoftwareRule { Id=id,Name="Fixture",Kind="Installed",InstallId="Exact-Install-Id",ProductName="Fixture",Publisher="Fixture Publisher",Reason="Test",Source="Local test" }; }
    static T Clone<T>(T value) { return MaintenanceJson.Serializer().Deserialize<T>(MaintenanceJson.Encode(value)); }
    static void Pump(int ms) { var watch=Stopwatch.StartNew(); while(watch.ElapsedMilliseconds<ms) { Application.DoEvents(); System.Threading.Thread.Sleep(10); } }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.U1)] static extern bool CreateSymbolicLink(string link,string target,int flags);
    [STAThread] public static int Main()
    {
        Application.EnableVisualStyles(); string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"test-data","maintenance-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Console.WriteLine("ARTIFACTS "+root);
        try
        {
            var db=new SoftwareDatabase(Path.Combine(root,"state"));
            Check("eight seed families load from embedded catalog",db.Catalog.Rules.Count==8&&db.Catalog.Rules.Select(r=>r.Id).Distinct().Count()==8);
            Check("catalog persisted outside executable",File.Exists(db.CatalogPath));
            var installed=Custom("local.test"); var incoming=new SoftwareCatalog { Rules=new List<SoftwareRule> { installed } };
            db.Commit(db.MergePreview(incoming)); Check("incremental import keeps seed catalog",db.Catalog.Rules.Count==9);
            Reject("same revision with changed content rejected",()=>{ var c=Clone(incoming); c.Rules[0].Name="Changed"; db.MergePreview(c); });
            var next=Clone(incoming); next.Rules[0].Revision=2; next.Rules[0].Reason="Reviewed update"; db.Commit(db.MergePreview(next));
            Check("higher revision updates single ID",db.Catalog.Rules.Count==9&&db.Catalog.Rules.Last().Revision==2);
            Reject("older revision cannot downgrade",()=>db.MergePreview(incoming));
            Reject("duplicate rule IDs rejected",()=>SoftwareDatabase.Validate(new SoftwareCatalog { Rules=new List<SoftwareRule> { installed,installed } }));
            Reject("component removal cannot be added through imported JSON",()=>{ var r=Clone(installed); r.Kind="Component"; SoftwareDatabase.Validate(new SoftwareCatalog { Rules=new List<SoftwareRule> { r } }); });
            Reject("compiled removal root cannot be broadened",()=>{ var c=SoftwareDatabase.Bundled(); c.Rules[0].RelativeRoot=".."; SoftwareDatabase.Validate(c); });
            Reject("reserved ID cannot change action type",()=>{ var c=SoftwareDatabase.Bundled(); var r=Custom(c.Rules[0].Id); SoftwareDatabase.Validate(new SoftwareCatalog { Rules=new List<SoftwareRule> { r } }); });
            Reject("registry path traversal rejected",()=>{ var r=Clone(installed); r.InstallId="..\\Run"; SoftwareDatabase.Validate(new SoftwareCatalog { Rules=new List<SoftwareRule> { r } }); });
            Reject("unqualified software name is insufficient",()=>{ var r=Clone(installed); r.Publisher=""; SoftwareDatabase.Validate(new SoftwareCatalog { Rules=new List<SoftwareRule> { r } }); });
            Reject("wildcard extension ID rejected",()=>{ var r=Clone(installed); r.Kind="ChromeExtension"; r.ExtensionId="*"; SoftwareDatabase.Validate(new SoftwareCatalog { Rules=new List<SoftwareRule> { r } }); });
            var app=new InstalledApp { Name="Fixture",Publisher="Fixture Publisher",Id="Exact-Install-Id" };
            Check("installed match requires all three identifiers",SoftwareScanner.MatchInstalled(installed,app));
            app.Id="Different"; Check("same display name cannot match another install ID",!SoftwareScanner.MatchInstalled(installed,app)); app.Id=installed.InstallId; app.Publisher="Unrelated";
            Check("same ID cannot match another publisher",!SoftwareScanner.MatchInstalled(installed,app));
            var f=new SoftwareFinding { Key="fingerprint-key",RuleId=installed.Id,Fingerprint="v1",Status="待确认",Actionable=true };
            db.State.Ignored[f.Key]=f.Fingerprint; Check("ignore suppresses unchanged version",!db.Pending(f)); f.Fingerprint="v2"; Check("new fingerprint requests review again",db.Pending(f));
            db.State.AllowedRules.Add(installed.Id); Check("permanent keep takes precedence over new fingerprint",!db.Pending(f)); db.Save();
            var restarted=new SoftwareDatabase(db.DirectoryPath); Check("keep and ignore survive restart",restarted.State.AllowedRules.Contains(installed.Id)&&restarted.State.Ignored.ContainsKey(f.Key));
            DateTime localNow=DateTime.Now; db.State.LastAttempt=localNow; db.State.LastSuccess=localNow; db.Save(); var timeReload=new SoftwareDatabase(db.DirectoryPath);
            Check("daily scheduling timestamps retain local wall clock after restart",timeReload.State.LastAttempt.ToString("yyyy-MM-dd HH:mm:ss")==localNow.ToString("yyyy-MM-dd HH:mm:ss")&&timeReload.State.LastSuccess.Kind==DateTimeKind.Local);
            string corruptRoot=Path.Combine(root,"corrupt-state"); var corruptDb=new SoftwareDatabase(corruptRoot); corruptDb.State.Latest.Findings.Add(null); corruptDb.Save(); var safeReload=new SoftwareDatabase(corruptRoot);
            Check("corrupt findings do not break app startup or silently schedule cleanup",safeReload.Warning.Length>0&&!safeReload.State.DailyEnabled&&safeReload.State.Latest.Findings.Count==0);
            restarted.State.AllowedRules.Clear(); restarted.State.DisabledRules.Add(installed.Id); Check("disabled rule cannot enter cleanup queue",!restarted.Pending(f));
            var now=new DateTime(2026,11,14,20,1,0); db.State.DailyEnabled=true; db.State.LastAttempt=DateTime.MinValue; db.State.LastSuccess=DateTime.MinValue;
            Check("first start has a due scan",db.Due(now)); db.State.LastSuccess=now; db.State.Latest.CatalogHash=db.CatalogHash; db.State.LastAttempt=now;
            Check("same day does not repeatedly scan",!db.Due(now.AddMinutes(30)));
            db.State.LastAttempt=now.AddDays(-1); db.State.LastSuccess=now.Date.AddHours(19); Check("daily scheduled boundary triggers after earlier manual scan",db.Due(now));
            db.State.LastSuccess=now.AddDays(-3); Check("missed day is caught up before next evening",db.Due(now.Date.AddHours(8)));
            db.State.DailyEnabled=false; Check("disabled schedule never runs automatically",!db.Due(now)); db.State.DailyEnabled=true;
            db.State.LastAttempt=now; Check("failed scan retries at most hourly",!db.Due(now.AddMinutes(10))); db.State.LastAttempt=now.AddHours(-2);
            db.State.LastSuccess=now; db.State.Latest.CatalogHash="older-catalog"; Check("catalog update schedules new inspection",db.Due(now));
            string xml=DailySoftwareTask.Xml(@"C:\Space & Place\PopupGuard.exe",@"C:\Data & Space",20,0); var doc=new XmlDocument(); doc.LoadXml(xml);
            var followup=new SoftwareDatabase(Path.Combine(root,"followup"));
            var waiting=new SoftwareFinding { Key="pending-component",RuleId="component.quickfolder",Name="Fixture pending removal",Status="等待重启",Root=Path.Combine(root,"gone") };
            followup.State.Latest.CatalogHash=followup.CatalogHash; followup.State.Latest.Findings.Add(waiting); followup.State.LastAttempt=now; followup.State.LastSuccess=now;
            Check("pending reboot is rechecked on startup before next daily boundary",!followup.Due(now.AddMinutes(10))&&followup.StartupRecheckNeeded);
            followup.State.DailyEnabled=false; Check("startup recheck respects disabled background checks",!followup.StartupRecheckNeeded); followup.State.DailyEnabled=true;
            var errorScan=new SoftwareScan { CatalogHash=followup.CatalogHash }; errorScan.Errors.Add("fixture read error");
            followup.ApplyScan(errorScan,now.AddMinutes(10)); Check("failed inventory cannot create a successful recheck",followup.State.Results.Count==0&&followup.State.LastSuccess==now);
            followup.State.Latest.Findings.Add(waiting);
            var residual=new SoftwareScan { CatalogHash=followup.CatalogHash }; residual.Findings.Add(new SoftwareFinding { Key=waiting.Key,RuleId=waiting.RuleId,Status="安装登记残留" });
            followup.ApplyScan(residual,now.AddMinutes(20)); Check("remaining registration is not reported removed",followup.State.Results.Count==0&&followup.State.Latest.Findings.Count==1);
            followup.State.Latest.Findings.Clear(); followup.State.Latest.Findings.Add(waiting);
            followup.State.Latest.Findings.Add(new SoftwareFinding { Key="pending-browser",RuleId="chrome.zenithstart",Name="Fixture extension",Status="等待浏览器复查" });
            var emptyScan=new SoftwareScan { CatalogHash=followup.CatalogHash };
            followup.ApplyScan(emptyScan,now.AddMinutes(30)); Check("successful recheck clears pending items and records both outcomes",followup.State.Latest.Findings.Count==0&&followup.State.Results.Count==2&&followup.State.Results.All(r=>r.Status=="复查未检出"));
            followup.ApplyScan(new SoftwareScan { CatalogHash=followup.CatalogHash },now.AddMinutes(31)); Check("repeated clean scan does not duplicate outcome records",followup.State.Results.Count==2&&!followup.StartupRecheckNeeded);
            followup.State.Results.Add(new CleanupResult { Key="worker-pending",Status="等待浏览器重启" });
            Check("pending worker results also request startup recheck",followup.StartupRecheckNeeded);
            followup.ApplyScan(new SoftwareScan { CatalogHash=followup.CatalogHash },now.AddMinutes(32)); Check("worker pending result gets updated after a full successful scan",followup.State.Results.Last().Status=="复查未检出");
            followup.State.Results.Add(new CleanupResult { Key="changed-scope",Status="等待重启" }); followup.State.Latest.CatalogHash="previous-scope";
            followup.ApplyScan(new SoftwareScan { CatalogHash=followup.CatalogHash },now.AddMinutes(33)); Check("changed catalog scope cannot falsely confirm removal",followup.State.Results.Last().Status=="等待重启");
            using(var startup=new MaintenanceController(Path.Combine(root,"startup-recheck"),null)) {
                startup.Database.State.Latest.CatalogHash=startup.Database.CatalogHash; startup.Database.State.Latest.Findings.Add(waiting); startup.Database.State.LastAttempt=DateTime.Now; startup.Database.State.LastSuccess=DateTime.Now;
                startup.RefreshView(); Check("opening fresh inspection view does not repeat its scan",!startup.Busy);
                startup.ScanOnStartup(); var deadline=Stopwatch.StartNew(); while(startup.Busy&&deadline.ElapsedMilliseconds<15000) Pump(20);
                Check("startup runs pending recheck even when daily scan is not due",!startup.Busy&&startup.Database.State.Latest.CatalogHash==startup.Database.CatalogHash&&!startup.Database.State.Latest.Findings.Any(x=>x.Key==waiting.Key)&&startup.Database.State.Results.Any(r=>r.Key==waiting.Key&&r.Status=="复查未检出"));
            }
            Check("scheduler XML escapes paths",xml.Contains("&amp;")&&doc.DocumentElement!=null);
            Check("scheduler uses no stored password or elevated task",xml.Contains("InteractiveToken")&&xml.Contains("LeastPrivilege")&&!xml.Contains("HighestAvailable"));
            Check("scheduler catches up without waking computer",xml.Contains("<StartWhenAvailable>true")&&xml.Contains("<WakeToRun>false"));
            Check("scheduler runs both daily and on login",xml.Contains("CalendarTrigger")&&xml.Contains("LogonTrigger")&&xml.Contains("--daily-scan"));
            Reject("invalid scheduled time rejected",()=>DailySoftwareTask.Xml("x","y",25,0));
            string extension="cfpedblmbakdamfceochcemgphgebnkf";
            var policy=SoftwareCleanup.MergeExtensionPolicy("{\"*\":{\"installation_mode\":\"allowed\"},\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\":{\"installation_mode\":\"force_installed\"},\""+extension+"\":{\"custom\":\"keep\"}}",extension);
            Check("extension removal preserves wildcard and unrelated policy",policy.ContainsKey("*")&&policy.ContainsKey("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
            var entry=SoftwareScanner.Object(policy[extension]); Check("extension removal preserves other per-ID properties",(string)entry["custom"]=="keep"&&(string)entry["installation_mode"]=="removed");
            Reject("malformed existing browser policy is never overwritten",()=>SoftwareCleanup.MergeExtensionPolicy("[]",extension));
            Reject("extension removal cannot target wildcard",()=>SoftwareCleanup.MergeExtensionPolicy("{}","*"));
            Reject("quoted command argument cannot break out",()=>SoftwareCleanup.Quote("C:\\x\" --extra"));
            Check("task cleanup accepts only exclusively owned executable actions",SoftwareCleanup.TaskOwned(new[]{@"C:\Fixture\updater.exe",@"C:\Fixture\child\helper.exe"},@"C:\Fixture"));
            Check("mixed task actions are preserved",!SoftwareCleanup.TaskOwned(new[]{@"C:\Fixture\updater.exe",@"C:\Unrelated\keep.exe"},@"C:\Fixture"));
            Check("task sibling-prefix does not match",!SoftwareCleanup.TaskOwned(new[]{@"C:\FixtureBackup\updater.exe"},@"C:\Fixture"));
            Check("empty or relative task command cannot match",!SoftwareCleanup.TaskOwned(new[]{"updater.exe"},@"C:\Fixture")&&!SoftwareCleanup.TaskOwned(new string[0],@"C:\Fixture"));
            string tree=Path.Combine(root,"delete-fixture"),neighbor=Path.Combine(root,"neighbor"); Directory.CreateDirectory(Path.Combine(tree,"child")); Directory.CreateDirectory(neighbor);
            File.WriteAllText(Path.Combine(tree,"child","payload.txt"),"synthetic test"); File.WriteAllText(Path.Combine(neighbor,"keep.txt"),"must remain");
            Reject("path traversal cannot escape confirmed directory",()=>SafeTree.CheckPath(tree,Path.Combine(tree,"..","neighbor","keep.txt")));
            Reject("Windows directory cannot be a cleanup root",()=>SafeTree.Enumerate(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
            Reject("Program Files cannot be a cleanup root",()=>SafeTree.Enumerate(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)));
            Check("scoped cleanup enumerates known tree",SafeTree.Enumerate(tree).Count==2);
            var removed=new CleanupResult(); Check("scoped cleanup succeeds for fixture",SoftwareCleanup.DeleteTree(tree,removed)==0&&!Directory.Exists(tree));
            Check("scoped cleanup preserves neighboring user data",File.ReadAllText(Path.Combine(neighbor,"keep.txt"))=="must remain");
            string linkRoot=Path.Combine(root,"link-fixture"); Directory.CreateDirectory(linkRoot); string link=Path.Combine(linkRoot,"outside");
            bool madeLink=CreateSymbolicLink(link,neighbor,3);
            if(!madeLink)
            {
                string command="New-Item -ItemType Junction -Path '"+link.Replace("'","''")+"' -Target '"+neighbor.Replace("'","''")+"' -ErrorAction Stop | Out-Null";
                using(var p=Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe"),"-NoProfile -NonInteractive -Command "+SoftwareCleanup.Quote(command)) { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden })) { p.WaitForExit(10000); madeLink=Directory.Exists(link)&&(File.GetAttributes(link)&FileAttributes.ReparsePoint)!=0; }
            }
            if(madeLink)
            {
                Reject("reparse child blocks entire cleanup before any deletion",()=>SoftwareCleanup.DeleteTree(linkRoot,new CleanupResult()));
                Check("symlink target remains untouched",File.Exists(Path.Combine(neighbor,"keep.txt"))); if(Directory.Exists(link)) Directory.Delete(link);
            }
            else { skipped++; Console.WriteLine("SKIP symbolic link fixture unavailable: "+Marshal.GetLastWin32Error()); }
            Check("pending deletion normalizes Windows queue prefix",PendingRemoval.Normalize(@"*1\??\"+Path.Combine(neighbor,"keep.txt"))==PendingRemoval.Normalize(Path.Combine(neighbor,"keep.txt")));
            var request=new CleanupRequest { UserSid=SoftwareCleanup.UserSid,Catalog=SoftwareDatabase.Bundled(),Selected=new List<SoftwareFinding> { f } }; request.CatalogHash=MaintenanceJson.Digest(MaintenanceJson.Encode(request.Catalog));
            SoftwareCleanup.ValidateRequest(request); Check("valid batch request accepted",true);
            var bad=Clone(request); bad.UserSid="S-1-0-0"; Reject("another Windows account cannot reuse batch approval",()=>SoftwareCleanup.ValidateRequest(bad));
            bad=Clone(request); bad.Created=DateTime.UtcNow.AddHours(-1); Reject("stale batch approval rejected",()=>SoftwareCleanup.ValidateRequest(bad));
            bad=Clone(request); bad.CatalogHash="tampered"; Reject("batch catalog tampering rejected",()=>SoftwareCleanup.ValidateRequest(bad));
            string savedRequest=Path.Combine(root,"request.json"); MaintenanceJson.Save(savedRequest,request); string approvedHash=Safety.HashFile(savedRequest);
            Check("request identity is checked against the exact bytes read",MaintenanceJson.ReadVerified<CleanupRequest>(savedRequest,approvedHash).UserSid==SoftwareCleanup.UserSid);
            File.AppendAllText(savedRequest," "); Reject("changed request bytes cannot reuse approval",()=>MaintenanceJson.ReadVerified<CleanupRequest>(savedRequest,approvedHash));
            FixtureUninstall(root);
            using(var host=new Form { Size=new System.Drawing.Size(1100,690) })
            using(var controller=new MaintenanceController(Path.Combine(root,"ui"),host))
            {
                Theme.Form(host); host.Text="软件巡检界面验证";
                controller.Database.State.Latest.Findings.Add(new SoftwareFinding { Name="Synthetic fixture / 测试软件",RuleId="component.quickfolder",Category="测试数据",Root=neighbor,Key="ui-test",Evidence="用于检查界面布局，未操作真实软件。",Detail="仅测试",Actionable=true,Fingerprint="fixture" });
                var panel=new MaintenancePanel(controller); host.Controls.Add(panel); host.ResumeLayout(true); host.Show(); Pump(300);
                Check("maintenance panel fills host client area",panel.Width==host.ClientSize.Width&&panel.Height==host.ClientSize.Height);
                using(var bitmap=new System.Drawing.Bitmap(host.Width,host.Height)) { host.DrawToBitmap(bitmap,new System.Drawing.Rectangle(System.Drawing.Point.Empty,bitmap.Size)); bitmap.Save(Path.Combine(root,"maintenance-panel.png")); }
                Check("software maintenance panel renders",new FileInfo(Path.Combine(root,"maintenance-panel.png")).Length>1000); host.Close();
            }
            using(var confirmation=new CleanupConfirmation(new List<SoftwareFinding> { f }))
            {
                confirmation.Show(); Pump(150); var text=confirmation.Controls.OfType<TextBox>().Single();
                Check("cleanup confirmation exposes full scope in a laid-out panel",text.Width==confirmation.ClientSize.Width&&text.Height>200);
                Check("cleanup confirmation has no default accept button",confirmation.AcceptButton==null&&confirmation.DialogResult==DialogResult.None);
                using(var bitmap=new System.Drawing.Bitmap(confirmation.Width,confirmation.Height)) { confirmation.DrawToBitmap(bitmap,new System.Drawing.Rectangle(System.Drawing.Point.Empty,bitmap.Size)); bitmap.Save(Path.Combine(root,"cleanup-confirmation.png")); }
                confirmation.Close();
            }
        }
        catch(Exception e) { Check("unhandled exception: "+e,false); }
        Console.WriteLine("RESULT "+passed+" passed / "+failed+" failed / "+skipped+" skipped"); return failed==0?0:1;
    }
    static void FixtureUninstall(string root)
    {
        string id="PopupGuard-Test-"+Guid.NewGuid().ToString("D"),keyPath=Uninstall.RegistryPath+"\\"+id;
        string folder=Path.Combine(root,"software-fixture"),exe=Path.Combine(folder,"MaintenanceFixture.exe"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,"test-fixture-only.txt"),"Synthetic fixture; safe to remove"); File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"MaintenanceFixture.exe"),exe);
        try
        {
            using(var hive=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64)) using(var key=hive.CreateSubKey(keyPath))
            {
                key.SetValue("DisplayName","PopupGuard Synthetic Uninstall Test"); key.SetValue("Publisher","PopupGuard Test Fixture"); key.SetValue("InstallLocation",folder); key.SetValue("UninstallString",SoftwareCleanup.Quote(exe)+" "+id);
            }
            var rule=new SoftwareRule { Id="local.fixture",Name="Synthetic fixture",InstallId=id,ProductName="PopupGuard Synthetic Uninstall Test",Publisher="PopupGuard Test Fixture",Kind="Installed" };
            var catalog=new SoftwareCatalog { Rules=new List<SoftwareRule> { rule } }; var scan=SoftwareScanner.Scan(catalog);
            Check("registry fixture scan uses exact ID and publisher",scan.Findings.Count==1&&scan.Findings[0].Actionable);
            var found=scan.Findings[0]; string initialCatalog=MaintenanceJson.Encode(catalog); var request=new CleanupRequest { UserSid=SoftwareCleanup.UserSid,Catalog=catalog,CatalogHash=scan.CatalogHash,Selected=new List<SoftwareFinding> { Clone(found) } };
            SourceListBridge(root,found.App,exe,keyPath);
            request.Selected[0].Fingerprint="changed";
            var rejected=SoftwareCleanup.Execute(request,Path.Combine(root,"stale-batch"),null);
            Check("changed fingerprint never launches uninstaller",rejected[0].Status=="已跳过"&&!File.Exists(Path.Combine(folder,"uninstaller-ran.txt")));
            request.Selected[0]=Clone(found); request.Selected[0].Root=Environment.SystemDirectory; request.Selected[0].App.Command="cmd.exe /c anything";
            Check("catalog identity stable after unrelated deserialization",initialCatalog==MaintenanceJson.Encode(catalog)&&request.CatalogHash==MaintenanceJson.Digest(MaintenanceJson.Encode(catalog)));
            string job=Path.Combine(root,"cleanup-jobs","valid-batch"),requestPath=Path.Combine(job,"request.json"); Directory.CreateDirectory(job); MaintenanceJson.Save(requestPath,request);
            string command="--cleanup-request "+SoftwareCleanup.Quote(requestPath)+" --request-sha256 "+Safety.HashFile(requestPath)+" --data-dir "+SoftwareCleanup.Quote(root);
            using(var worker=Process.Start(new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"PopupGuard.exe"),command) { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden }))
            { bool finished=worker.WaitForExit(20000); Check("real cleanup worker completes fixture batch",finished&&worker.ExitCode==0); }
            var result=MaintenanceJson.Read<List<CleanupResult>>(Path.Combine(job,"results.json"));
            Check("batch uses fresh system identity, not supplied executable or path",File.Exists(Path.Combine(folder,"uninstaller-ran.txt")));
            Check("installer success with leftover files is not reported clean",result[0].Status=="仍有残留");
            Check("batch writes operation audit",File.Exists(Path.Combine(job,"results.json")));
            Check("post-uninstall scan no longer finds install entry",SoftwareScanner.Scan(catalog).Findings.Count==0);
        }
        finally { using(var hive=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64)) hive.DeleteSubKeyTree(keyPath,false); }
    }
    static void SourceListBridge(string root,InstalledApp app,string exe,string keyPath)
    {
        string data=Path.Combine(root,"source-bridge");
        using(var context=new GuardContext(data,false))
        {
            context.Monitor.Paused=true; context.Monitor.Store.Settings.AutoPrompt=false;
            int uiThread=System.Threading.Thread.CurrentThread.ManagedThreadId; bool notificationsOnUi=true;
            context.Maintenance.Changed+=delegate { notificationsOnUi=notificationsOnUi&&System.Threading.Thread.CurrentThread.ManagedThreadId==uiThread; };
            var window=new WindowInfo { Process=new ProcessInfo { Pid=-123,Created=DateTime.UtcNow.ToFileTimeUtc(),Path=exe,Name="Synthetic fixture" },Title="演示广告（测试）",Class="Fixture",Hash=Safety.HashFile(exe) };
            var evidence=new Activity { Window=window,Chain=new List<ProcessInfo> { window.Process },EvidenceReady=true };
            using(var review=new ReviewForm(context,window,evidence))
            {
                review.StartPosition=FormStartPosition.Manual; review.Location=new System.Drawing.Point(-10000,-10000);
                review.Show(); Pump(900);
                var button=(Button)typeof(ReviewForm).GetField("addSoftware",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(review);
                var note=(Label)typeof(ReviewForm).GetField("softwareNote",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(review);
                var sources=(ListBox)typeof(ReviewForm).GetField("apps",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(review);
                Check("popup review offers cleanup-list option for a verified installed source",button.Enabled&&sources.SelectedItem is InstalledApp&&((InstalledApp)sources.SelectedItem).Id==app.Id);
                using(var image=new System.Drawing.Bitmap(review.Width,review.Height)) { review.DrawToBitmap(image,new System.Drawing.Rectangle(System.Drawing.Point.Empty,image.Size)); image.Save(Path.Combine(root,"review-software-option.png")); }
                using(var hive=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64)) using(var key=hive.OpenSubKey(keyPath,true)) key.SetValue("Publisher","Changed publisher");
                Reject("source cannot be added after installed publisher changes",()=>context.Maintenance.AddInstalled(app,"Test source"));
                Check("rejected source leaves catalog unchanged",context.Maintenance.Database.Catalog.Rules.Count==8);
                using(var hive=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,RegistryView.Registry64)) using(var key=hive.OpenSubKey(keyPath,true)) key.SetValue("Publisher",app.Publisher);
                var rule=context.Maintenance.AddInstalled((InstalledApp)sources.SelectedItem,"用户在弹窗来源核对中加入（测试）");
                for(int i=0;i<100&&context.Maintenance.Busy;i++) Pump(100);
                Check("adding source immediately scans into maintenance findings",!context.Maintenance.Busy&&context.Maintenance.Database.State.Latest.Findings.Any(f=>f.RuleId==rule.Id));
                Check("adding source never executes its uninstaller",!File.Exists(Path.Combine(Path.GetDirectoryName(exe),"uninstaller-ran.txt"))&&Uninstall.StillRegistered(app)&&context.Maintenance.Database.State.Results.Count==0);
                Check("review recognizes newly added source without duplicate button",!button.Enabled&&button.Text.Contains("已在"));
                int count=context.Maintenance.Database.Catalog.Rules.Count; int revision=rule.Revision;
                context.Maintenance.Database.State.AllowedRules.Add(rule.Id); context.Maintenance.Database.State.DisabledRules.Add(rule.Id); context.Maintenance.Database.Save(); context.Maintenance.Notify();
                var same=context.Maintenance.AddInstalled(app,"Repeated from installed-software picker");
                for(int i=0;i<100&&context.Maintenance.Busy;i++) Pump(100);
                Check("popup and installed-software entry points reuse one rule",same.Id==rule.Id&&same.Revision==revision&&context.Maintenance.Database.Catalog.Rules.Count==count);
                Pump(100);
                Check("repeat add preserves explicit keep and disabled preferences",context.Maintenance.Database.State.AllowedRules.Contains(rule.Id)&&context.Maintenance.Database.State.DisabledRules.Contains(rule.Id)&&note.Text.Contains("永久保留"));
                Check("asynchronous maintenance notifications return to UI thread",notificationsOnUi);
                if(!note.Text.Contains("永久保留")) Console.WriteLine("KEEP DEBUG disposed="+review.IsDisposed+" allowed="+context.Maintenance.Database.State.AllowedRules.Contains(rule.Id)+" disabled="+context.Maintenance.Database.State.DisabledRules.Contains(rule.Id)+" note="+note.Text);
                Check("source-added rule persists after reload",new SoftwareDatabase(data).Catalog.Rules.Any(r=>r.Id==rule.Id&&r.InstallId==app.Id));
                review.Close();
            }
            var unmapped=Clone(window); unmapped.Process.Path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"PopupGuard.Demo.exe"); unmapped.Hash=Safety.HashFile(unmapped.Process.Path);
            using(var review=new ReviewForm(context,unmapped,new Activity { Window=unmapped,Chain=new List<ProcessInfo> { unmapped.Process },EvidenceReady=true }))
            {
                review.StartPosition=FormStartPosition.Manual; review.Location=new System.Drawing.Point(-10000,-10000);
                review.Show(); Pump(700); var button=(Button)typeof(ReviewForm).GetField("addSoftware",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(review);
                Check("unmapped popup cannot create guessed software blacklist",!button.Enabled); review.Close();
            }
            context.ExitThread();
        }
        var db=new SoftwareDatabase(Path.Combine(root,"known-component-bridge")); var known=db.Catalog.Rules.First(r=>r.Id=="component.quickfolder");
        db.State.AllowedRules.Add(known.Id);
        var knownApp=new InstalledApp { Id=known.InstallId,Name=known.ProductName,Publisher=known.Publisher,Location=SoftwareScanner.ComponentRoot(known) };
        Check("known component source reuses reviewed recipe and keep preference",db.AddInstalled(knownApp,"Test source").Id==known.Id&&db.Catalog.Rules.Count==8&&db.State.AllowedRules.Contains(known.Id));
    }
}
