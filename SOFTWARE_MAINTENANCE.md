# 软件巡检 · v0.4.1

## 使用流程

广告出现时按 **Ctrl+↓** 点选，在 **核对弹窗来源** 窗口下方选中来源软件，再点击 **加入软件清理黑名单…**，可以直接接入本页的每日巡检。也可从历史截图打开同一个核对窗口。加入前重新核对来源文件指纹及当前安装项；加入后立即只读扫描，清理仍需再次确认。找不到可靠安装项时该选项不可用，可点击 **打开软件巡检**，从已安装软件列表按名称添加。重复添加复用既有规则，不解除已有永久保留或停用设置。

在“软件巡检”页保存每日设置，可注册当前用户的 Windows 每日任务，默认 20:00。任务同时有登录触发器，程序按最后完成时间判断是否补扫，避免每次登录都重复检查。首次运行和名单变化后会检查；读取异常不记为成功，至少间隔一小时重试。任务不保存密码、不以 SYSTEM 身份执行、不唤醒电脑。未登录或关机时不能扫描。卸载整个弹窗卫士前，请先取消每日检查并保存，以删除其任务。

程序在后台盘点安装注册项、名单指定的组件文件、两个已知驱动注册项及 Chrome 的 Default/Profile N 配置。只读取浏览器扩展设置，不读取密码、Cookie 或浏览历史。软件判断不使用 PID；PID 只用于清理当时核实归属的组件进程。没有全盘遍历和未知恶意代码分析。

“扫描与待处理”显示命中证据及范围。勾选后打开确认窗，清楚展示整个软件还是特定组件。清理助手请求 Windows 管理员权限，仅处理确认时的名单、目标和文件指纹；任何身份变化都跳过。用户拒绝 UAC 时不会执行清理。临时权限只用于这一批，守护程序继续以普通权限运行。

名单和软件清理白名单独立于弹窗窗口黑白名单。永久保留对该软件规则的所有版本生效；忽略仅针对当前目标指纹。永久保留或停用会阻止后续批量选择，但不会恢复已删除的软件，也不会撤销先前已设置的 Chrome 移除策略。

## 内置来源

| 规则 ID | 范围 | 依据 |
|---|---|---|
| component.360huabao | 360 画报／壁纸专属组件目录 | 本机运行/自启动记录、360 组件说明 |
| component.quickfolder | QuickFolder 固定目录 | 本机组件文件；该目录曾有 Defender Rootkit 检出 |
| component.winboost | WinBoost 固定目录 | 同上 |
| component.vaultify | 天津邦兴利达的 Vaultify 固定目录 | 本机文件、历史检测及火绒分析；不泛指同名软件 |
| component.keytask-bzbz | 百战霸主 keytask 推广组件 | 本机安装项及组件子目录 |
| component.masterwallpaper | 大师壁纸专属目录 | 本机程序/自启动信息及已核实残留版本 |
| chrome.zenithstart | cfpedblmbakdamfceochcemgphgebnkf | 本机扩展配置与推广逻辑 |
| chrome.daybreak | ndnibnapfmcjeomefllboaahbngildga | 同上 |

目录和文件名等特征说明身份相符，不是病毒判定。读取被 Defender 拦截时记录异常，不绕过防护。仅有同名目录、无法验证的文件或孤立驱动注册项不开放一键删除。已在 Windows 删除队列内的残留显示“等待重启”，不会重复处理；已经设置 Chrome 移除策略的 ID 显示等待浏览器复查。

## 增量维护

“添加已安装软件”使用精确的安装项 ID + 显示名称 + 发布者。软件更新更换安装项 ID 或发布者时可能需要补充规则；该行为有意避免扩大匹配造成误卸载。“添加扩展 ID”只接受 32 位 a–p 的 Chrome ID。

导入 JSON 时先预览变更，按 Id 合并。相同 Id 的修改必须增加 Revision，拒绝降级、重复 ID、无效扩展 ID 和不完整的软件身份。保留本地白名单/停用项，不因导入名单而清理软件。可通过导出保存名单；本版没有远程更新源、联网同步或签名分发服务。

普通软件规则示例（演示 ID，不匹配实际软件）：

```json
{
  "SchemaVersion": 1,
  "Revision": 1,
  "Rules": [
    {
      "Id": "local.example",
      "Revision": 1,
      "Name": "示例软件",
      "Kind": "Installed",
      "Category": "用户不需要",
      "InstallId": "Example-Exact-Uninstall-Key",
      "ProductName": "示例软件",
      "Publisher": "示例发布者",
      "Reason": "用户确认不需要",
      "Source": "本机安装项核查"
    }
  ]
}
```

扩展规则使用 `Kind: "ChromeExtension"` 和 `ExtensionId`。规则不允许嵌入 PowerShell、任意卸载命令或递归删除路径。能直接清理文件/驱动的 `Component` 配方必须与本版本编译内置定义一致；导入文件不能扩充其删除权限。这部分新增清理配方需代码审核和程序升级。

## 清理与验证边界

- 普通安装软件：读取并核验系统登记的卸载命令；MSI 使用产品 ID 重建 `/x /passive /norestart`。自定义卸载器仍可能弹出交互界面。等待三分钟仍不退出时，停止后续卸载，保留进程让用户处理；退出码 0 仍需检查安装项和目录，不直接报成功。
- 内置组件：先验证完整目录树，再停止该目录中的非系统宿主进程。仅移除精确指向该目录的 Run/RunOnce、COM 加载值、服务及全部动作都属于该目录的计划任务。检查目录联接、路径边界及每次删除的对象；不删除共享根目录，不强行卸载内核驱动。文件占用时使用 Windows 重启删除队列。保留注册值、服务值、任务 XML 和文件清单，不保留文件内容备份；目录内设置与缓存会删除。
- Chrome：合并现有用户 `ExtensionSettings`，只将所选 ID 的 `installation_mode` 设为 `removed`，保留其他策略和扩展。遇到冲突的系统策略停止处理。Chrome 刷新策略或重启后才真正卸载；本版不改写 Secure Preferences、不强制关闭浏览器，也不承诺清理非标准安装位置的每个文件。本地策略可能导致 Chrome 显示“由贵单位管理”。
- 本版不把历史父进程链当作捆绑安装证据，也不根据一个品牌把该品牌所有产品卸载。没有自动停用 Defender、提升长期常驻权限或自行重启的逻辑。
- 清理结果分别记录已卸载/已清理、仍有残留、等待重启/浏览器重启、身份变化跳过和失败。复查找不到已等待的目标、扫描无读取异常且范围一致，才标记“复查未检出”。它只说明规则覆盖的对象状态，不能证明整机无其他威胁。

## 记录与调试

`data/software-rules.json` 保存名单；`software-state.json` 保存时间、命中证据、保留/忽略和最近 500 项结果。每次批量清理在 `data/cleanup-jobs/日期-ID/` 保存请求、结果、登记信息备份。现阶段批次目录不自动删除，可在确认不再需要后手动归档。记录包含本机路径，导出或分享前请核对。

`PopupGuard.exe --software-report 绝对JSON路径 --data-dir 绝对数据目录` 可执行一次只读匹配并导出结果，不运行卸载、不设置计划任务。`--configure-daily` 按已保存时间注册每日任务，`--daily-scan --tray` 仅触发到期扫描或转交已有守护实例。普通用户无需使用命令行。

运行 `dist\PopupGuard.MaintenanceTests.exe` 可复现巡检测试。运行产生的 `artifacts`、`test-data` 和本机扫描报告不提交到仓库。清理执行用随机安装项和自带测试卸载器验证，不为测试重复卸载真实软件。

## 技术依据

- [Windows 安装项与产品标识](https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key)
- [Windows Task Scheduler 任务注册](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskfolder-registertaskdefinition)
- [错过执行时间后的补跑](https://learn.microsoft.com/en-us/windows/win32/taskschd/tasksettings-startwhenavailable)
- [MSI 卸载与禁止自动重启参数](https://learn.microsoft.com/en-us/windows/win32/msi/standard-installer-command-line-options)
- [Chrome 扩展管理及 removed 模式](https://support.google.com/chrome/a/answer/9867568?hl=en)
