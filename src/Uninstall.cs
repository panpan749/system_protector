using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PopupGuard
{
    public class InstalledApp
    {
        public string Name,Publisher,Location,Command,Key,Id,Match; public RegistryHive Hive; public RegistryView View;
        public bool Msi; public int AncestorDepth;
        public override string ToString() { return Name+" — "+Match; }
    }
    public class LaunchPlan { public string FileName,Arguments,Reason; public bool Valid { get { return string.IsNullOrEmpty(Reason); } } }
    public static class Uninstall
    {
        public const string RegistryPath=@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        public static List<InstalledApp> Find(List<ProcessInfo> chain)
        {
            var found=new List<InstalledApp>();
            foreach(var hive in new[]{RegistryHive.CurrentUser,RegistryHive.LocalMachine})
            foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})
            try
            {
                using(var root=RegistryKey.OpenBaseKey(hive,view)) using(var uninstall=root.OpenSubKey(RegistryPath))
                {
                    if(uninstall==null) continue;
                    foreach(string keyName in uninstall.GetSubKeyNames())
                    try
                    {
                        using(var key=uninstall.OpenSubKey(keyName))
                        {
                            if(key==null || Convert.ToInt32(key.GetValue("SystemComponent",0))==1) continue;
                            string name=Convert.ToString(key.GetValue("DisplayName","")); if(name.Length==0) continue;
                            string location=Environment.ExpandEnvironmentVariables(Convert.ToString(key.GetValue("InstallLocation",""))).Trim().Trim('"');
                            string icon=Environment.ExpandEnvironmentVariables(Convert.ToString(key.GetValue("DisplayIcon",""))).Trim();
                            if(icon.StartsWith("\"")) { int end=icon.IndexOf('"',1); icon=end>0?icon.Substring(1,end-1):""; }
                            else icon=Regex.Replace(icon,@",\s*-?\d+$","");
                            // Never infer ownership from broad shared folders or an uninstall program alone.
                            int depth=-1; string match="";
                            for(int i=0;i<chain.Count;i++)
                            {
                                if(Safety.ProtectedReason(chain[i])!="") continue;
                                if(SafeInstallRoot(location) && Safety.IsWithin(chain[i].Path,location)) { depth=i; match="安装目录匹配"; break; }
                                if(Path.IsPathRooted(icon)&&string.Equals(icon,chain[i].Path,StringComparison.OrdinalIgnoreCase)) { depth=i; match="程序图标路径精确匹配"; break; }
                            }
                            if(depth<0) continue;
                            found.Add(new InstalledApp { Name=name,Publisher=Convert.ToString(key.GetValue("Publisher","")),Location=location,
                                Command=Convert.ToString(key.GetValue("UninstallString","")),Key=RegistryPath+"\\"+keyName,Id=keyName,Hive=hive,View=view,
                                Msi=Convert.ToInt32(key.GetValue("WindowsInstaller",0))==1,AncestorDepth=depth,Match=(depth==0?"弹窗进程":"父进程第 "+depth+" 层")+" / "+match });
                        }
                    } catch { }
                }
            } catch { }
            return found.GroupBy(x=>x.Hive+"|"+x.Key).Select(g=>g.First()).OrderBy(x=>x.AncestorDepth).ToList();
        }
        public static bool SafeInstallRoot(string location)
        {
            try
            {
                if(string.IsNullOrWhiteSpace(location)||!Path.IsPathRooted(location)) return false;
                string full=Path.GetFullPath(location).TrimEnd('\\');
                if(full.Length<=3 || full.Split('\\').Length<3) return false;
                foreach(var folder in new[]{Environment.SpecialFolder.Windows,Environment.SpecialFolder.ProgramFiles,Environment.SpecialFolder.ProgramFilesX86,
                    Environment.SpecialFolder.UserProfile,Environment.SpecialFolder.LocalApplicationData,Environment.SpecialFolder.ApplicationData,Environment.SpecialFolder.CommonApplicationData,Environment.SpecialFolder.DesktopDirectory})
                { string broad=Environment.GetFolderPath(folder); if(!string.IsNullOrEmpty(broad)&&string.Equals(full,broad.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)) return false; }
                return !Safety.IsWithin(full,Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            } catch { return false; }
        }
        public static LaunchPlan Plan(InstalledApp app)
        {
            Guid guid;
            if(app.Msi && Guid.TryParse(app.Id,out guid)) return new LaunchPlan { FileName=Path.Combine(Environment.SystemDirectory,"msiexec.exe"),Arguments="/x {"+guid.ToString()+"}" };
            string command=Environment.ExpandEnvironmentVariables(app.Command??"").Trim(); string file,args;
            if(command.StartsWith("\""))
            {
                int end=command.IndexOf('"',1); if(end<0) return Invalid("卸载命令引号不完整"); file=command.Substring(1,end-1); args=command.Substring(end+1).Trim();
            }
            else
            {
                int space=command.IndexOf(' '); file=space<0?command:command.Substring(0,space); args=space<0?"":command.Substring(space+1);
                if(!file.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)) return Invalid("未加引号的路径不明确，请使用 Windows 已安装应用");
            }
            if(!Path.IsPathRooted(file) || file.StartsWith(@"\\") || !file.EndsWith(".exe",StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return Invalid("卸载程序必须是存在的本地 EXE 绝对路径");
            if(!SafeInstallRoot(app.Location)||!Safety.IsWithin(file,app.Location)) return Invalid("卸载程序不在已匹配的安装目录内，请使用 Windows 已安装应用");
            if(Safety.ProtectedReason(new ProcessInfo { Pid=-1,Created=1,Path=file })!="") return Invalid("不直接运行系统宿主或脚本卸载命令");
            return new LaunchPlan { FileName=file,Arguments=args };
        }
        static LaunchPlan Invalid(string reason) { return new LaunchPlan { Reason=reason }; }
        public static bool StillRegistered(InstalledApp app)
        {
            using(var root=RegistryKey.OpenBaseKey(app.Hive,app.View)) using(var key=root.OpenSubKey(app.Key)) return key!=null;
        }
        public static bool CommandUnchanged(InstalledApp app)
        {
            using(var root=RegistryKey.OpenBaseKey(app.Hive,app.View)) using(var key=root.OpenSubKey(app.Key))
                return key!=null && Convert.ToString(key.GetValue("UninstallString",""))==app.Command && Convert.ToString(key.GetValue("DisplayName",""))==app.Name
                    && Environment.ExpandEnvironmentVariables(Convert.ToString(key.GetValue("InstallLocation",""))).Trim().Trim('"')==app.Location
                    && (Convert.ToInt32(key.GetValue("WindowsInstaller",0))==1)==app.Msi;
        }
    }
}
