using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Windows.Forms;

namespace PopupGuard
{
    public sealed class Dashboard : Form
    {
        readonly GuardContext context; readonly Monitor monitor;
        readonly ListView activity=new ListView(),rules=new ListView(),presets=new ListView();
        readonly TabControl tabs=new PageStack { Dock=DockStyle.Fill,Margin=Padding.Empty };
        readonly NavigationRail navigation=new NavigationRail { Dock=DockStyle.Fill,Margin=Padding.Empty };
        readonly Label stats=new Label(),status=new Label(),title=new Label();
        readonly ScreenshotPreview preview=new ScreenshotPreview();
        readonly CheckBox onlyPictures=new CheckBox { Text="仅有截图",AutoSize=true,Margin=new Padding(16,10,8,0) };
        readonly Label historyCount=new Label { AutoSize=true,Margin=new Padding(8,10,0,0),ForeColor=Theme.Muted };
        readonly TextBox search=new TextBox { Width=220,BorderStyle=BorderStyle.None,AccessibleName="搜索弹窗记录",Margin=new Padding(8) };
        readonly Button pause,review; readonly Label source=new Label { Dock=DockStyle.Top,Height=48,Padding=new Padding(16,14,0,0),Font=new Font("Microsoft YaHei UI",11,FontStyle.Bold),AutoEllipsis=true };
        bool quitting,refreshing;
        public Dashboard(GuardContext context)
        {
            this.context=context; monitor=context.Monitor; Motion.Preference=monitor.Store.Settings.UiAnimations;
            Theme.Form(this); Text="弹窗卫士"; Size=new Size(1180,800); MinimumSize=new Size(1000,700); DoubleBuffered=true;
            var root=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,RowCount=3,Padding=Padding.Empty,Margin=Padding.Empty };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,192)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute,132)); root.RowStyles.Add(new RowStyle(SizeType.Percent,100)); root.RowStyles.Add(new RowStyle(SizeType.Absolute,38)); Controls.Add(root);
            root.Controls.Add(navigation,0,0); root.SetRowSpan(navigation,3);
            var header=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=2,RowCount=2,Padding=new Padding(26,18,26,8),Margin=Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.Percent,60)); header.RowStyles.Add(new RowStyle(SizeType.Percent,40));
            title.Text="弹窗记录"; title.Dock=DockStyle.Fill; title.Font=new Font(Font.FontFamily,23,FontStyle.Bold); title.TextAlign=ContentAlignment.MiddleLeft;
            header.Controls.Add(title,0,0); var actions=new FlowLayoutPanel { AutoSize=true,Dock=DockStyle.Fill,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(0,8,0,0),WrapContents=false };
            var pick=Theme.Button("点选广告",()=>context.Pick(null),true); pick.AccessibleDescription="快捷键 Ctrl 加向下箭头"; actions.Controls.Add(pick);
            pause=Theme.Button("暂停守护",()=>{ monitor.Paused=!monitor.Paused; RefreshData(); },false); actions.Controls.Add(pause); header.Controls.Add(actions,1,0);
            stats.Dock=DockStyle.Fill; stats.TextAlign=ContentAlignment.MiddleLeft; stats.ForeColor=Theme.Accent; header.Controls.Add(stats,0,1); header.SetColumnSpan(stats,2); root.Controls.Add(header,1,0);
            root.Controls.Add(tabs,1,1); tabs.Margin=new Padding(26,0,26,0);
            TabPage events=Page("弹窗记录"),ruleTab=Page("黑白名单"),presetTab=Page("广告规则"),softwareTab=Page("软件巡检"),settings=Page("设置"); tabs.TabPages.AddRange(new[]{events,ruleTab,presetTab,softwareTab,settings});
            navigation.Selected+=i=>tabs.SelectedIndex=i;
            tabs.SelectedIndexChanged+=delegate { navigation.SelectPage(tabs.SelectedIndex); title.Text=tabs.SelectedTab.Text; };
            navigation.SelectPage(0); softwareTab.Controls.Add(new MaintenancePanel(context.Maintenance));

            var historyLayout=new TableLayoutPanel { Dock=DockStyle.Fill,ColumnCount=1,RowCount=4,Margin=Padding.Empty };
            historyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,52)); historyLayout.RowStyles.Add(new RowStyle(SizeType.Percent,42)); historyLayout.RowStyles.Add(new RowStyle(SizeType.Percent,58)); historyLayout.RowStyles.Add(new RowStyle(SizeType.Absolute,60)); events.Controls.Add(historyLayout);
            var filter=new FlowLayoutPanel { Dock=DockStyle.Fill,WrapContents=false,Padding=new Padding(8,5,0,0) };
            var searchBox=new Panel { Width=224,Height=34,BackColor=Theme.Paper,Padding=new Padding(8,7,8,4),Margin=new Padding(6,3,0,0) }; search.Dock=DockStyle.Fill; search.BackColor=Theme.Paper; searchBox.Controls.Add(search);
            filter.Controls.Add(new Label { Text="搜索",AutoSize=true,ForeColor=Theme.Muted,Margin=new Padding(4,10,0,0) }); filter.Controls.Add(searchBox); filter.Controls.Add(onlyPictures); filter.Controls.Add(historyCount); historyLayout.Controls.Add(filter,0,0);
            search.TextChanged+=delegate { RefreshData(); }; onlyPictures.CheckedChanged+=delegate { RefreshData(); };
            ConfigureList(activity,new[]{"时间","状态","截图","窗口标题","来源程序","判定依据"},new[]{130,90,100,220,170,280}); historyLayout.Controls.Add(activity,0,1);
            var evidencePanel=new Panel { Dock=DockStyle.Fill,BackColor=Color.White,Margin=new Padding(0,8,0,0) }; preview.Dock=DockStyle.Fill; evidencePanel.Controls.Add(preview); evidencePanel.Controls.Add(source); historyLayout.Controls.Add(evidencePanel,0,2);
            activity.SelectedIndexChanged+=delegate { if(!refreshing) UpdatePreview(); };
            activity.DoubleClick+=delegate { if(activity.SelectedItems.Count>0) context.Review((Activity)activity.SelectedItems[0].Tag); };
            var eventActions=Actions(); review=Theme.Button("核对并加入名单",()=>{ if(activity.SelectedItems.Count>0) context.Review((Activity)activity.SelectedItems[0].Tag); },true); eventActions.Controls.Add(review);
            eventActions.Controls.Add(Theme.Button("恢复窗口",()=>monitor.RestoreAll(),false));
            eventActions.Controls.Add(Theme.Button("清空记录",()=>{ if(MessageBox.Show(this,"删除全部历史记录和截图？已有名单将保留。","清空记录",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)==DialogResult.Yes) monitor.ClearHistory(); },false)); historyLayout.Controls.Add(eventActions,0,3);

            ConfigureList(rules,new[]{"类型","程序","窗口标题","匹配范围","状态"},new[]{90,220,230,150,90}); ruleTab.Controls.Add(rules);
            var ruleActions=Actions(); ruleActions.Dock=DockStyle.Bottom; ruleTab.Controls.Add(ruleActions);
            ruleActions.Controls.Add(Theme.Button("启用 / 停用",()=>ChangeRule(false),false)); ruleActions.Controls.Add(Theme.Button("删除规则",()=>ChangeRule(true),false));
            ConfigureList(presets,new[]{"状态","来源","规则","组件进程","匹配条件"},new[]{75,100,210,250,350}); presetTab.Controls.Add(presets);
            var presetToggle=new CheckBox { Dock=DockStyle.Top,Height=48,Padding=new Padding(14,0,0,0),Text="启用广告规则库",Checked=monitor.Store.Settings.EnablePresets };
            bool changing=false; presetToggle.CheckedChanged+=delegate { if(changing) return; bool previous=monitor.Store.Settings.EnablePresets; try { monitor.Store.Settings.EnablePresets=presetToggle.Checked; monitor.Store.Save(); monitor.Rescan(); RefreshData(); } catch(Exception ex) { monitor.Store.Settings.EnablePresets=previous; changing=true; presetToggle.Checked=previous; changing=false; MessageBox.Show(ex.Message,"设置未保存"); } }; presetTab.Controls.Add(presetToggle);
            var presetActions=Actions(); presetActions.Dock=DockStyle.Bottom; presetTab.Controls.Add(presetActions);
            presetActions.Controls.Add(Theme.Button("启用 / 停用",ChangePreset,false)); presetActions.Controls.Add(Theme.Button("查看条件",ShowPreset,false));
            presetActions.Controls.Add(Theme.Button("查看来源",()=>{ if(presets.SelectedItems.Count>0) Process.Start(new ProcessStartInfo(((PresetRule)presets.SelectedItems[0].Tag).SourceUrl) { UseShellExecute=true }); },false)); presets.DoubleClick+=delegate { ShowPreset(); };
            BuildSettings(settings);
            status.Dock=DockStyle.Fill; status.Padding=new Padding(26,8,0,0); status.ForeColor=Theme.Muted; status.AutoEllipsis=true; root.Controls.Add(status,1,2);
            FormClosing+=delegate(object s,FormClosingEventArgs e) { if(!quitting&&e.CloseReason==CloseReason.UserClosing) { e.Cancel=true; Hide(); } };
            Shown+=delegate { using(var g=CreateGraphics()) foreach(var list in new[]{activity,rules,presets}) foreach(ColumnHeader c in list.Columns) c.Width=(int)(c.Width*g.DpiX/96f); };
            monitor.Changed+=RefreshData; ResumeLayout(true); RefreshData();
        }
        static TabPage Page(string text) { return new TabPage(text) { BackColor=Color.White,Padding=Padding.Empty }; }
        static FlowLayoutPanel Actions() { return new FlowLayoutPanel { Dock=DockStyle.Fill,Height=60,Padding=new Padding(10,10,0,0),WrapContents=false,AutoScroll=true }; }
        static void ConfigureList(ListView list,string[] columns,int[] widths) { Theme.StyleList(list); list.MultiSelect=false; for(int i=0;i<columns.Length;i++) list.Columns.Add(columns[i],widths[i]); }
        void BuildSettings(TabPage page)
        {
            var options=new FlowLayoutPanel { Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,Padding=new Padding(24),AutoScroll=true }; page.Controls.Add(options);
            options.Controls.Add(new Label { Text="守护偏好",AutoSize=true,Font=new Font(Font.FontFamily,14,FontStyle.Bold),Margin=new Padding(0,0,0,24) });
            var prompt=new CheckBox { Text="发现可疑弹窗时提醒定位",Checked=monitor.Store.Settings.AutoPrompt,AutoSize=true,Margin=new Padding(0,0,0,24) };
            prompt.CheckedChanged+=delegate { monitor.Store.Settings.AutoPrompt=prompt.Checked; monitor.Store.Save(); }; options.Controls.Add(prompt);
            var capture=new CheckBox { Text="保存弹窗截图",Checked=monitor.Store.Settings.CaptureScreenshots,AutoSize=true,Margin=new Padding(0,0,0,8) };
            capture.CheckedChanged+=delegate { monitor.Store.Settings.CaptureScreenshots=capture.Checked; monitor.Store.Save(); }; options.Controls.Add(capture);
            options.Controls.Add(new Label { Text="仅存本机，最多 200 张 / 100 MB / 30 天",AutoSize=true,ForeColor=Theme.Muted,Margin=new Padding(20,0,0,24) });
            var startup=new CheckBox { Text="登录后在后台运行",Checked=context.IsStartup(),Enabled=context.StartupAvailable,AutoSize=true,Margin=new Padding(0,0,0,24) }; bool changing=false;
            if(!startup.Enabled) startup.Text="登录自启动暂不可用";
            startup.CheckedChanged+=delegate { if(changing) return; try { context.SetStartup(startup.Checked); } catch(Exception e) { changing=true; startup.Checked=context.IsStartup(); changing=false; MessageBox.Show(e.Message,"设置未保存"); } }; options.Controls.Add(startup);
            var animation=new CheckBox { Text="界面动效",Checked=Motion.Preference,AutoSize=true,Margin=new Padding(0,0,0,24) };
            animation.CheckedChanged+=delegate { Motion.Preference=animation.Checked; monitor.Store.Settings.UiAnimations=animation.Checked; monitor.Store.Save(); }; options.Controls.Add(animation);
            options.Controls.Add(Theme.Button("打开数据文件夹",()=>Process.Start(new ProcessStartInfo(monitor.Store.DirectoryPath) { UseShellExecute=true }),false));
            options.Controls.Add(Theme.Button("使用帮助",()=>MessageBox.Show(this,"Ctrl + ↓ 点选广告，Esc 取消。\r\n双击历史截图可放大，核对来源后加入名单。\r\n\r\n拦截会隐藏窗口；恢复后临时放行 5 分钟。软件清理需另外确认。\r\n关闭面板后继续守护；托盘菜单可彻底退出。\r\n\r\n锁屏、受保护内容或瞬间消失的弹窗可能无法截图。旧版区域图可能只有背景，已消失的内容无法补拍。\r\n浏览器内广告和系统通知需在所属应用中处理。","使用帮助"),false));
        }
        void UpdatePreview() {
            var a=activity.SelectedItems.Count>0?(Activity)activity.SelectedItems[0].Tag:null;
            preview.ShowEvidence(a,monitor.Evidence,search.Text.Trim().Length>0?"没有匹配记录":onlyPictures.Checked?"还没有可用截图":"还没有弹窗记录");
            source.Text=a==null?"截图预览":a.Window.Process.Name+(string.IsNullOrEmpty(a.Window.Title)?"":"   "+a.Window.Title); review.Enabled=a!=null;
        }
        void ChangeRule(bool remove) { if(rules.SelectedItems.Count==0) return; var r=(Rule)rules.SelectedItems[0].Tag; if(remove) monitor.Store.Settings.Rules.Remove(r); else r.Enabled=!r.Enabled; monitor.Store.Save(); monitor.Rescan(); RefreshData(); }
        void ChangePreset() { if(presets.SelectedItems.Count==0) return; var r=(PresetRule)presets.SelectedItems[0].Tag; var previous=monitor.Store.Settings.DisabledPresetIds.ToList(); if(previous.Contains(r.Id)) monitor.Store.Settings.DisabledPresetIds.Remove(r.Id); else monitor.Store.Settings.DisabledPresetIds.Add(r.Id); try { monitor.Store.Save(); } catch { monitor.Store.Settings.DisabledPresetIds=previous; throw; } monitor.Rescan(); RefreshData(); }
        void ShowPreset() { if(presets.SelectedItems.Count==0) return; var r=(PresetRule)presets.SelectedItems[0].Tag; MessageBox.Show(this,r.Name+"\r\n\r\n组件："+string.Join(" / ",r.ProcessNames)+"\r\n目录："+string.Join(" / ",r.PathSegments)+"\r\n标题："+r.Conditions+"\r\n\r\n条件需同时满足；正常主窗口与安全提醒排除。\r\n"+r.SourceUrl,"广告规则详情"); }
        public void RefreshData()
        {
            if(IsDisposed) return;
            stats.Text=(monitor.Paused?"● 守护已暂停":"● 正在守护")+"     已拦截 "+monitor.BlockCount+" 次     可恢复 "+monitor.Hidden.Count+" 个窗口";
            stats.ForeColor=monitor.Paused?Color.FromArgb(158,105,35):Theme.Accent; pause.Text=monitor.Paused?"恢复守护":"暂停守护";
            string selected=activity.SelectedItems.Count>0?((Activity)activity.SelectedItems[0].Tag).Id:"";
            var images=new HashSet<string>(monitor.History.Where(a=>monitor.Evidence.HasImage(a.ScreenshotFile)).Select(a=>a.Id));
            string query=search.Text.Trim(); var visible=monitor.History.Where(a=>(!onlyPictures.Checked||images.Contains(a.Id))&&(query.Length==0||(a.Window.Process.Name+" "+a.Window.Title+" "+a.Window.Process.Path+" "+a.Action).IndexOf(query,StringComparison.CurrentCultureIgnoreCase)>=0)).ToList();
            historyCount.Text=visible.Count+" 条记录";
            refreshing=true; activity.BeginUpdate(); activity.Items.Clear(); foreach(var a in visible) {
                string imageState=images.Contains(a.Id)?(a.ScreenshotMethod==EvidenceStore.WindowCaptureMethod?"窗口截图":"旧版区域图"):string.IsNullOrEmpty(a.ScreenshotFile)?"未截图":"已清理";
                var item=new ListViewItem(new[]{a.Time.ToString("MM-dd HH:mm:ss"),a.Action,imageState,a.Window.Title,a.Window.Process.Name,a.Reason}) { Tag=a }; activity.Items.Add(item); if(a.Id==selected) item.Selected=true;
            }
            if(activity.SelectedItems.Count==0&&activity.Items.Count>0) activity.Items[0].Selected=true; activity.EndUpdate(); refreshing=false; UpdatePreview();
            string selectedRule=rules.SelectedItems.Count>0?((Rule)rules.SelectedItems[0].Tag).Id:"";
            rules.BeginUpdate(); rules.Items.Clear(); foreach(var r in monitor.Store.Settings.Rules) { var item=new ListViewItem(new[]{r.Allow?"放行":"拦截",Path.GetFileName(r.Path),r.Title,r.AnyTitle?"同类所有标题":"精确标题",r.Enabled?"启用":"停用"}) { Tag=r }; rules.Items.Add(item); if(r.Id==selectedRule) item.Selected=true; } rules.EndUpdate();
            string selectedPreset=presets.SelectedItems.Count>0?((PresetRule)presets.SelectedItems[0].Tag).Id:"";
            presets.BeginUpdate(); presets.Items.Clear(); foreach(var r in monitor.Presets.Rules) { var item=new ListViewItem(new[]{monitor.Presets.Enabled(r,monitor.Store.Settings)?"启用":"停用",r.Family,r.Name,string.Join(" / ",r.ProcessNames),r.Conditions}) { Tag=r }; presets.Items.Add(item); if(r.Id==selectedPreset) item.Selected=true; } presets.EndUpdate();
            status.Text=monitor.Store.Warning!=""?monitor.Store.Warning:!monitor.HookAvailable?"实时监听不可用，正在定期扫描":!context.HotkeyAvailable?"快捷键被占用，可点击“点选广告”":"截图与记录仅保存在此电脑";
        }
        public void Quit() { quitting=true; Close(); }
        public void ShowSoftware() { tabs.SelectedIndex=3; }
        protected override void Dispose(bool disposing) { if(disposing) monitor.Changed-=RefreshData; base.Dispose(disposing); }
    }
}
