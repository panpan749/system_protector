using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

namespace PopupGuard
{
    public sealed class PresetRule
    {
        public string Id,Name,Family,SourceUrl;
        public string[] ProcessNames,PathSegments,TitleExact,TitlePrefixes;
        public bool Matches(WindowInfo w)
        {
            if(w==null || Safety.ProtectedReason(w.Process)!="" || !PopupShape(w)) return false;
            // Names and whole directory segments are hints, not verified publisher signatures.
            // Never match by brand, PID, title keywords, or a path substring alone.
            string filename=Path.GetFileName(w.Process.Path);
            if(!ProcessNames.Contains(filename,StringComparer.OrdinalIgnoreCase)) return false;
            string[] segments=Path.GetDirectoryName(w.Process.Path).Split(new[]{'\\','/'},StringSplitOptions.RemoveEmptyEntries);
            if(!PathSegments.Any(p=>segments.Contains(p,StringComparer.OrdinalIgnoreCase))) return false;
            string title=(w.Title??"").Trim();
            string[] excluded={"设置","选项","登录","账号","安装","卸载","更新","升级","病毒","木马","风险","防护","权限","保存","驱动","修复",".doc",".xls",".ppt",".pdf",".txt"};
            if(excluded.Any(x=>title.IndexOf(x,StringComparison.OrdinalIgnoreCase)>=0)) return false;
            return TitleExact.Contains(title,StringComparer.OrdinalIgnoreCase) || TitlePrefixes.Any(p=>title.StartsWith(p,StringComparison.OrdinalIgnoreCase));
        }
        static bool PopupShape(WindowInfo w)
        {
            double scale=Math.Max(96,w.Dpi)/96.0;
            // A resizable or maximizable main window must never be hidden by presets.
            if((w.Style&(0x40000|0x10000))!=0) return false;
            if(w.Bounds.Width<120*scale || w.Bounds.Height<60*scale || w.Bounds.Width>900*scale || w.Bounds.Height>700*scale) return false;
            return (w.ExStyle&(8|0x80))!=0 || (w.Style&0xC00000)==0;
        }
        public string Conditions { get { return string.Join(" / ",TitleExact.Concat(TitlePrefixes.Select(p=>p+"…")))+"；仅小型推广窗口"; } }
    }
    public sealed class PresetCatalog
    {
        public readonly List<PresetRule> Rules;
        public PresetCatalog()
        {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("PopupGuard.presets.json"))
            {
                if(stream==null) throw new InvalidDataException("缺少预置广告规则资源");
                using(var reader=new StreamReader(stream)) Rules=new JavaScriptSerializer().Deserialize<List<PresetRule>>(reader.ReadToEnd());
            }
            if(Rules==null || Rules.Count==0 || Rules.Select(r=>r.Id).Distinct().Count()!=Rules.Count || Rules.Any(r=>
                string.IsNullOrEmpty(r.Id)||string.IsNullOrEmpty(r.Name)||string.IsNullOrEmpty(r.Family)||
                r.ProcessNames==null||r.ProcessNames.Length==0||r.ProcessNames.Any(string.IsNullOrWhiteSpace)||
                r.PathSegments==null||r.PathSegments.Length==0||r.PathSegments.Any(string.IsNullOrWhiteSpace)||
                r.TitleExact==null||r.TitlePrefixes==null||r.TitleExact.Length+r.TitlePrefixes.Length==0||
                r.TitleExact.Concat(r.TitlePrefixes).Any(string.IsNullOrWhiteSpace)||r.SourceUrl==null||!r.SourceUrl.StartsWith("https://")))
                throw new InvalidDataException("预置广告规则格式错误");
        }
        public bool Enabled(PresetRule rule,Settings settings) { return settings.EnablePresets&&!settings.DisabledPresetIds.Contains(rule.Id); }
        public PresetRule Match(WindowInfo w,Settings settings) { return Rules.FirstOrDefault(r=>Enabled(r,settings)&&r.Matches(w)); }
    }
}
