# 预置广告规则与源头关闭方式

调研日期：2026-11-13。对应版本：v0.3。

本次加入 9 条本地规则、7 类来源。组件名单来自厂商说明、安全厂商研究和开源项目；标题候选及防误拦条件由本项目组合设计。没有安装这些厂商的全部历史/当前版本进行实测，自动化测试使用自带模拟程序。材料证明的是特定时期的组件或推广行为，**不表示同品牌全部软件、全部版本都是恶意软件**，也没有据此得出市场覆盖率。

## 规则清单和来源

| ID | 识别组件 | 依据与局限 |
|---|---|---|
| `360-news` | `360tray.exe` / `360safe.exe`，360Safe 目录内的小型推广资讯窗口 | [360 桌面弹窗关闭说明](https://bbs.360.cn/thread-15889638-1-1.html)，另参阅 [360 社区关闭指南](https://bbs.360.cn/thread-16136256-1-1.html)。只处理卫士精选、今日趣看等明确标题，排除安全、防护、修复和设置提醒。 |
| `360-wallpaper-promo` | `360huabao.exe`，360huabao 等目录下的推广窗口 | [360 官方社区关闭和卸载说明](https://bbs.360.cn/thread-16008209-1-1.html)（2021，后有更新）明确组件名及壁纸/屏保开关。本机观察到 5.4.34.0 正在运行；当时没有可见广告样本，未据此验证标题覆盖。普通壁纸主界面和全屏屏保不匹配。 |
| `2345-haozip-helper` | `Helper_Haozip.exe` | [火绒双 11 推广分析](https://www.huorong.cn/document/tech/vir_report/723)（2018）识别好压推广组件及 Helper_2345 路径。材料较旧，当前版本可能已变，不泛化成所有 Helper 程序。 |
| `wps-promotion` | `wpsnotify.exe` / `ksetopupmgr.exe` | [火绒广告开关说明](https://www.huorong.cn/document/tech/safety-classroom/1725)介绍 WPS 热点与推送开关；组件名另参考 [AdSentinel 原始规则](https://github.com/dai123x/AdSentinel/blob/main/windows/AdSentinel/assets/builtin_rules.json)。组合匹配 WPS 目录与热点/会员推广标题，不直接封禁整个 WPS。 |
| `flash-mini` | `MiniPage.exe`，必须位于 FlashCenter 等目录 | [PopKiller 社区规则](https://github.com/lmg325586/PopKiller/blob/master/community_rules.json)列出该组件。这里增加目录、标题、形态约束，避免同名程序被整进程封禁。 |
| `flash-promotion` | `FlashCenter.exe` / `FlashHelperService.exe` | 同上。只处理明确推广窗口，普通 Flash 主界面、更新和安装窗口排除；系统目录中的服务不处理。 |
| `ludashi-news` | `MiniNews.exe`，LuDaShi 等目录 | [GridinSoft 文件遥测](https://threatinfo.net/companies/ludashi.com)列出迷你新闻组件。文件样本资料不代表所有版本行为；限制目录和新闻标题。 |
| `kuaizip-promotion` | `KuaizipUpdate.exe`，KuaiZip 等目录 | [火绒快压传播分析](https://huorong.cn/document/tech/vir_report/1669)（2018）识别该组件。只处理推广标题，更新、安装界面排除；不封禁压缩软件主程序。 |
| `sogou-classic-promotion` | `biz_helper.exe`，SogouInput 等目录 | [火绒搜狗云控分析](https://www.huorong.cn/document/tech/vir_report/1845)（2025）识别推广组件，也报告系统 Toast 推送。本规则只适用于它自身创建、满足全部条件的独立小窗口，**不能拦截报告中的 Shell 托管 Toast**，不把系统通知宿主纳入黑名单。 |

所有精确标题、允许前缀、完整目录段和进程名均可在面板或 `data/presets.json` 查看。预置目录匹配不使用简单字符串包含，避免把 `FlashCenterBackup` 当成 `FlashCenter`。文件名和路径可能被伪装，不能当作数字签名或恶意判定。没有下载、执行或复制来源项目代码；未直接导入其宽泛规则，事实性组件名用来独立实现有限窗口规则。

## 拦截机制

白名单 → 用户确认的黑名单 → 启用的预置规则 → 未知窗口确认。预置规则要求以下条件全部成立：

1. 可读取同一进程实例、文件路径和创建时间，且不在受保护的系统/浏览器/脚本宿主范围内。
2. 组件文件名和完整目录段命中。
3. 标题精确命中，或符合列表中的明确推广前缀；排除设置、登录、安装、更新、安全提醒和常见文档扩展名。
4. 小型置顶/工具/无标题栏窗口，不可调整大小、不可最大化；排除全屏窗口。
5. 读取当前文件指纹后，再次验证窗口身份、标题及规则状态，确认没有新增白名单，再记录和请求隐藏。

命中后保存当前来源、规则 ID 和可用截图。锁屏或会话状态无法确认时，仅保留文字证据与未截图原因。没有终止进程、修改软件文件、阻止 EXE 加载或操作安全服务；恢复日志及五分钟临时放行照常生效。

## “不再启动弹窗”与隐藏窗口

当前守护器是在窗口创建/显示后识别并隐藏，可能闪现。要从源头阻止，需要关闭软件自己的推广开关，或停用明确的独立广告启动项。不能把两者描述为相同能力。

- 360 画报/壁纸：根据官方说明，可在设置中心关闭屏保和自动轮播，或关闭独立组件的登录启动。后者会影响壁纸自动启动，故默认保留。360 卫士主程序的广告开关不能保证控制所有独立模块。
- WPS：版本支持时，在高级设置中关闭 WPS 热点订阅、广告推送，依据上述火绒开关指南。
- 2345：输入法/看图等产品有不同开关，按对应软件设置关闭；本版未将所有 2345 产品主程序封禁。
- 搜狗系统通知：优先在输入法推荐设置中关闭；系统 Toast 需另做按应用身份的通知管理。本版没有对 ShellExperienceHost 等系统进程作处理。

本次不修改第三方软件自启动、服务、计划任务、防火墙或权限。卸载仍由用户核对安装项后单独确认。

## 验证和后续补充

自动化检查覆盖每条规则的典型匹配、同名程序不同目录、目录前缀碰撞、主界面、设置、安全提醒、文档标题、系统进程与浏览器保护；另外用隔离目录中的自带 Demo 实测预置拦截、白名单优先、全局/单条开关、恢复与截图历史。需要真实厂商弹窗样本来评估漏报/误报和扩大覆盖。可用 Ctrl+↓ 点选，或从历史截图创建精确规则；不会自动扩大目录或标题范围。

锁屏判断依据 [Microsoft WTSINFOEX_LEVEL1_W](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/ns-wtsapi32-wtsinfoex_level1_w)：Windows 10/11 会话需处于 active、unlocked，且输入桌面可访问才读取屏幕。Windows 7 的标记差异不在本程序支持范围内。
