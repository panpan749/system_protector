$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$packageDir = Join-Path $projectDir 'release\PopupGuard-v0.5.1-win-x64'
New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
foreach ($name in @('PopupGuard.exe','PopupGuard.Capture.exe','PopupGuard.Demo.exe')) {
    Copy-Item -LiteralPath (Join-Path $projectDir "dist\$name") -Destination $packageDir -Force
}
foreach ($name in @('README.md','RESEARCH.md','PRESET_RULES.md','SOFTWARE_MAINTENANCE.md')) {
    Copy-Item -LiteralPath (Join-Path $projectDir $name) -Destination $packageDir -Force
}
$quickstart = @'
弹窗卫士 v0.5.1

1. 双击 PopupGuard.exe。它会在系统托盘后台运行。
2. Ctrl + 向下箭头 或托盘“点选广告”，点击广告位置。
3. 核对截图和来源后选择“是广告：加入黑名单”。之后按该规则自动拦截。
4. 来源软件可匹配到安装项时，可选中并确认卸载。
5. 误拦截可在托盘恢复全部窗口，并在黑白名单停用规则。
6. 托盘“退出并恢复窗口”彻底退出；关闭面板仍然在后台运行。
7. “弹窗记录”会留存可疑弹窗区域图片；人不在时发生的弹窗也可稍后加入名单。
8. “广告规则”默认启用 9 条常见推广组件规则，可查看条件、调研来源并逐条停用。白名单优先。
9. “软件巡检”可维护软件名单、每日检查和批量确认清理。默认 20:00；点击“保存每日设置”登记系统任务，登录后可补扫。任务只扫描，清理另行确认。
10. 可以从已安装软件添加、按扩展 ID 添加，或导入增量名单；支持永久保留、忽略此版本及清理结果复查。
11. 点选广告或核对历史截图后，可选中来源软件并点击“加入软件清理黑名单…”，确认后进入每日巡检；不会因此立即卸载。
12. 历史列表显示截图状态和数量；勾选“只看有截图”查看现存图片。没有图片时会显示原因。解锁后仍显示的可疑窗口会重新采集，已消失的窗口不能补拍。
13. 新版直接采集目标窗口内容，支持 GPU 窗口；透明空白或捕获失败时不会保存背景冒充截图。旧图片标为“旧版区域图”，可能只有壁纸，无法事后补回。
14. 请保持 PopupGuard.Capture.exe 与主程序在同一目录。窗口截图需要 Windows 10 1903 或更新系统；Windows 可能在采集时显示边框。
15. 采集组件常驻预热，窗口出现后优先抓取第一张有效画面，截图随后保存完成。历史预览显示实际截图时间；已抓到的图片不会因为弹窗随后关闭而丢弃。

截图仅存本机，最多 200 张 / 100 MB / 30 天，可在设置关闭或清空。
锁屏、休眠或窗口瞬间消失时可能无法截图；升级前消失的弹窗无法补拍。

PopupGuard.Demo.exe 是无害的测试弹窗，两分钟后自动退出。
Run 登录自启动默认关闭，可在面板设置中开启；软件巡检任务另含登录补扫，在巡检页保存设置后生效。
README 中 dist\ 指源码构建输出目录；本便携包的 EXE 就在当前目录。

自动拦截命中预置规则或你确认过的桌面弹窗，不保证覆盖所有广告。
窗口出现后识别并隐藏，可能闪现；软件巡检只在另行确认清理后处理对应组件与加载项。
浏览器内部广告、Windows 通知、管理员窗口等存在明确限制。
不会自动结束进程，不会未经确认卸载软件。详细说明见 README.md。
'@
Set-Content -LiteralPath (Join-Path $packageDir '使用说明.txt') -Value $quickstart -Encoding UTF8
$referenceDir = Join-Path $packageDir 'data'
New-Item -ItemType Directory -Force -Path $referenceDir | Out-Null
Copy-Item -LiteralPath (Join-Path $projectDir 'data\presets.json') -Destination $referenceDir -Force
Copy-Item -LiteralPath (Join-Path $projectDir 'data\software-rules.json') -Destination $referenceDir -Force
$hashLines = Get-ChildItem -LiteralPath $packageDir -Filter '*.exe' | ForEach-Object { $digest = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256; "$($digest.Hash)  $($_.Name)" }
Set-Content -LiteralPath (Join-Path $packageDir 'SHA256SUMS.txt') -Value $hashLines -Encoding ASCII
Compress-Archive -LiteralPath $packageDir -DestinationPath (Join-Path $projectDir 'release\PopupGuard-v0.5.1-win-x64.zip') -Force
Write-Output (Join-Path $projectDir 'release\PopupGuard-v0.5.1-win-x64.zip')



