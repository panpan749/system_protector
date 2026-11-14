# 技术调研与首版取舍

调研日期：2026-11-13。目标平台为用户当前的 Windows x64 桌面。下文保留首版设计依据；v0.3 按用户要求补充预置广告规则，详细来源和覆盖限制见 [PRESET_RULES.md](PRESET_RULES.md)。当前策略为预置规则及用户确认来源自动隐藏，陌生来源先确认。

## 参考项目

| 项目 | 可借鉴思路 | 本项目的取舍 |
|---|---|---|
| [PopKiller](https://github.com/lmg325586/PopKiller) | 窗口样式、标题、程序路径等多特征；规则管理、日志及误拦截反馈 | 首版仅用启发式触发确认，不直接信任模型分数或远端规则 |
| [Popupstopper](https://github.com/Jgcole83/Popupstopper) | 区分传统窗口和系统通知；窗口到进程再到启动来源；托盘后台运行 | 实现窗口检测和进程链；通知数据库、任务调度器和服务关联留待后续 |
| [AdSentinel](https://github.com/dai123x/AdSentinel) | C# Windows 客户端、托盘和本地规则可支持较小部署体积 | 采用无第三方依赖的 WinForms，但不预置仅凭标题或进程名即自动处置的规则 |

这些仓库用于阅读设计思路，没有复制源码、模型、规则或发布二进制。公开说明不是对项目准确率、可信度或安全性的独立验证。

## 机制和依据

### 后台运行位置

使用当前登录用户会话内的托盘程序。Windows 服务位于隔离的 Session 0，不适合作为直接显示点选蒙版的唯一组件。若未来需要管理员级溯源，应另设最小权限代理，由桌面端与之通信。[Microsoft：Interactive Services](https://learn.microsoft.com/en-us/windows/win32/services/interactive-services)

### 窗口检测

`SetWinEventHook` 以 out-of-context 方式监听 SHOW 和 NAMECHANGE，保留托管回调引用并由 UI 消息循环接收。回调只入队，后续处理身份和规则；每两秒枚举用于弥补丢失事件或重新显示。不会注入目标程序。该 API 是事后事件通知，不能阻止窗口创建，因此仍可能短暂闪现。[Microsoft：SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook)

### 点选与 PID

半透明顶层蒙版接收完整鼠标点击，鼠标释放后隐藏蒙版，使用点坐标调用 `WindowFromPoint`，再取得顶层窗口和所属 PID。该 API 会忽略隐藏或禁用窗口，因此本版不声称覆盖每一种复杂控件或异形窗口。[Microsoft：WindowFromPoint](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-windowfrompoint)、[GetWindowThreadProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowthreadprocessid)

### 父进程链

Toolhelp 快照提供 `th32ParentProcessID`。每一跳同时核对创建时间，拒绝比子进程更晚创建的同 PID 进程；已缓存链保留当时验证的事实。对无法读取或已经退出且未观察到的父进程停止追踪，不编造来源，也不据此断定捆绑安装。进程创建时间核对只能防止常见 PID 复用误判，不能证明进程未遭注入或 PPID 未被指定/伪装。[Microsoft：PROCESSENTRY32W](https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/ns-tlhelp32-processentry32w)

### 黑名单身份

持久规则同时绑定 EXE 绝对路径、SHA-256、窗口类和标题。用户可明确扩大标题范围，但默认不屏蔽整个应用的所有窗口。白名单优先。浏览器和系统/命令宿主拒绝自动处置。软件更新后需要再次确认，以降低新版本复用旧规则的风险。哈希用于文件身份，不代表恶意鉴定，也不是数字签名验证。

### 卸载

读取系统安装项中的 `DisplayName`、`Publisher`、`InstallLocation`、`DisplayIcon`、`UninstallString` 等字段，通过安装目录边界或精确 EXE 图标路径关联到来源链。共享根目录不作归属依据。MSI 用产品 GUID 重建显式 `/x` 命令；其他安装器仅支持位于已匹配安装目录内的本地绝对 EXE 路径。未知或歧义命令转交 Windows 设置。执行前展示具体候选与命令，并再次检查安装项和文件指纹。[Microsoft：Uninstall Registry Key](https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key)

执行卸载器不等于卸载成功，界面分别报告退出码和安装项是否仍存在；不据此声称残留、持久化项或所有恶意组件已清除。没有静默批量卸载、手工递归删除目录或基于父 PID 自动删除软件的逻辑。

## 为什么本版不用视觉 AI 自动判恶意

图像型广告和无标题窗口确实可能需要 OCR/UI Automation/视觉模型。但缺少本机标注样本时难以建立可验证的误报率，模型也无法仅靠广告截图判断应卸载哪个程序。首版先实现人工反馈、证据链、局部规则与可恢复拦截，给后续模型提供可以审核的样本流程。

未来可分阶段加入：用户明确同意的局部图像样本、UI Automation 可访问文本、数字签名验证、ETW 进程创建事件、任务/服务启动来源关联、浏览器扩展与 Toast 来源设置。各类来源需要单独验证，不能通过关闭宿主进程统一处理。

## 首版验证范围

自动化测试覆盖规则匹配与范围、文件更换、白名单优先、PID 复用、过期父链、路径边界、安装器命令验证、配置损坏恢复，以及真实原生 Demo 窗口的监听、隐藏、恢复、重启日志恢复和蒙版点选。界面另有渲染截图检查，并针对本机高 DPI 和受限注册表权限修正。

不使用用户实际软件进行卸载测试；没有完整恶意样本集或长期真机误报/漏报率数据。多显示器混合 DPI、权限提升目标、不同广告框架的兼容性仍需要持续实测。

## v0.2：局部截图与历史确认

按用户要求改用 Ctrl + 向下箭头，并为可疑窗口保存本地图片。采用 `Graphics.CopyFromScreen` 直接读取窗口当前可见矩形；先校验窗口所属进程、创建时间和桌面可访问性，保存后复核窗口位置与身份。不会先存整屏，也不会调用目标窗口可能长期阻塞的 `PrintWindow`。这意味着被遮挡窗口只能保存当时可见的画面，不能还原遮挡后的内容。[Microsoft：CopyFromScreen](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.graphics.copyfromscreen)、[PrintWindow 的同步调用限制](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow)

截图前后检查输入桌面，锁屏或安全桌面不可访问时跳过并记录原因。历史保存原始 SHA-256 与父链，加载历史时不恢复 HWND；事后创建规则比较当前文件指纹和原记录，而不是用已复用的 PID 定位其他进程。测试覆盖来源进程退出后加名单、新进程再次出现被拦截、历史重载、截图关闭和数量/时限清理。

## v0.4.3：按窗口捕获合成画面

一次本地测试中的无标题 `Chrome_WidgetWin_1` 窗口，记录区域为 720 × 160、DPI 192，原 PNG 实际只有背景。此例证明“区域 PNG 保存成功”不足以证明弹窗内容采集成功；历史文件无法区分当时是透明占位窗口、首次绘制时机，还是图形采集路径未包含该窗口。

改用 Windows Graphics Capture 的 `CreateForWindow`，用 HWND 和已校验的 PID/创建时间选择单个窗口。使用 `CreateFreeThreaded`、D3D11 staging texture 和 WIC PNG 编码；原生组件独立进程运行，主程序设置超时，不采用桌面裁剪回退。透明或明显空白的帧在短时间内重试，失败记录原因，不冒充有效截图。主程序和组件均检查会话/输入桌面与窗口身份，遵守截图保护。

验证使用自建 DirectComposition GPU 通知：延迟 600 ms 才绘制文字及绿色标记，随后增加红色遮挡窗口，并更改标记颜色；断言采集到目标文字/色块、没有混入遮挡层且内容发生更新。另测透明空窗、截图保护、隐藏/过期进程身份、异步期间新增白名单与清空历史。可证明这些场景正常，不代表覆盖所有真实应用或全部渲染状态。

资料：[Microsoft 窗口捕获互操作](https://learn.microsoft.com/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createforwindow)、[无 UI 线程依赖的帧池](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframepool.createfreethreaded)、[Microsoft 捕获流程](https://learn.microsoft.com/en-us/windows/uwp/audio-video-camera/screen-capture)。

## v0.4.4：窗口出现后优先采集

延迟来源包括每次新建进程和 D3D 设备、取得有效帧后仍固定等待 180 ms、串行处理窗口，以及截图前保存父链/历史。改为一个常驻原生子进程，内部四个线程预热 D3D 设备，仅在请求到来时启动窗口采集会话。通过父子进程重定向的 stdin/stdout 传递窗口身份和输出路径，不设置公共管道或网络接口。退出主程序时终止组件；组件失联或超时会取消未完成请求，下一次请求重新启动组件。

WinEvent 的窗口显示事件优先进入 UI 消息队列，50 ms 计时器和每两秒枚举作为补充；最多同时处理 16 个窗口。先请求图片，再计算指纹和保存元数据。采集取得第一张通过透明度/变化量筛选的帧时，复核窗口 PID、创建时间、标题、类、可见性与桌面状态，记录采集时刻，然后冻结像素。此后的编码和持久化不再要求窗口继续存在。自动隐藏仍重新验证当前窗口和规则，采集期间添加白名单不会被忽略。

新增测试用隐藏且已绘制的 GPU 窗口触发实际显示事件，500 ms 后关闭；六个依次显示，再让一个透明空窗占用采集线程时同时显示三个。九张图均断言背景色、文字和标记色，确认窗口退出后仍可从历史加载实际采集时间和图片。两次本机运行均 9/9 成功，首轮中位 103.4 ms、最慢 152.6 ms，正式回归中位 90.0 ms、最慢 129.9 ms。测量从发送显示指令开始，直到像素冻结；不把 PNG 编码结束或列表刷新时间当成采集时间。

这些结果只验证本机模拟场景。渲染较慢、极短存续、桌面不可用、硬件/系统差异或超过并发容量仍可能漏截。失败必须保留明确原因，不能回退为壁纸截图，也不能从已消失窗口补拍历史画面。
