# 光标工坊 CursorStudio

Windows 全局鼠标光标替换工具。把整套自定义光标方案（.cur 静态 + .ani 动画）应用到**整个系统**，带注册表持久化、防还原守护、开机自启、托盘常驻、PNG 一键转换。

- 单个 exe，零依赖（用 Windows 自带的 .NET Framework 4.x 编译，任何 Win10/11 直接运行）
- 内置 6 套方案 × 14 个光标角色：5 套配色方案（含"忙碌 / 后台运行"24 帧动画）+ **小狗汪汪**角色方案
- 不需要管理员权限

## 快速开始

```
dist\CursorStudio.exe            # 打开主界面，点击方案卡片即应用
dist\CursorStudio.exe list       # 列出可用方案
dist\CursorStudio.exe apply sakura   # 命令行应用"樱花粉"
dist\CursorStudio.exe restore    # 恢复系统默认光标
dist\CursorStudio.exe selftest   # 冒烟测试（约 40 项断言）
```

主界面功能：

| 功能 | 说明 |
| --- | --- |
| 内置方案卡片 | 点击即应用：经典白 / 樱花粉 / 薄荷绿 / 暗夜紫 / 活力橙 / 小狗汪汪 |
| 上传 PNG | 任意图片转成 32×32 的 .cur 并应用到指定角色（自动设置热点） |
| 恢复系统默认 | 还原首次运行前备份的注册表内容，并直接把默认光标写回槽位 |
| 开机自启 | 写入 `HKCU\...\CurrentVersion\Run`，登录后自动应用当前方案 |
| 防还原守护 | 见下文"Windows 11 还原问题" |
| 托盘图标 | 双击打开主窗口；右键可恢复默认 / 退出 |

## 工作原理

按方案设计的三层机制实现，并在本机（Windows 11 build 26200，150% DPI）实测修正：

1. **注册表持久化**：把每个角色的 .cur/.ani 绝对路径写入 `HKCU\Control Panel\Cursors`（Arrow / IBeam / Wait / Hand / Size* 等全部角色），`(Default)` 写入方案名。重启 / 注销后由系统登录流程原生加载，无需本程序参与。
2. **立即生效**：`SetSystemCursor(hcur, OCR_xxx)` 直接覆盖 13 个系统光标槽位（32512–32516、32642–32650）。注意该 API 会"吞掉"传入句柄，每次重新应用都要重新 `LoadCursorFromFile`。
3. **防还原守护**：每 3 秒用 `GetCursorInfo` + `GetIconInfo` + `GetDIBits` 取当前全局光标的**实际像素签名**，与已应用方案的签名集合比对；不一致（被系统还原成经典样式）就重新应用。另有约 30 秒一次的无条件兜底重应用。

### 本机实测发现的四个关键事实

1. **`GetCursorInfo` 报告的句柄不变**：`SetSystemCursor` 替换的是槽位*内容*，报告的仍是共享句柄（如箭头固定为 `0x10003`）——所以不能用句柄比对做还原检测，必须比对像素。
2. **签名要取自加载后的句柄**：150% DPI 下 win32k 把 32px 光标缩放成 48px 存储，文件字节（32×32）与槽位内容（48×48）永远对不上；`LoadCursorFromFile` 返回的句柄已是缩放后的版本，`GetIconInfo` 它即得到与槽位一致的签名。
3. **`SystemParametersInfo(SPI_SETCURSORS)` 在本机失效**：无论注册表内容是什么都返回失败（`gle=6`）。因此应用方案以 `SetSystemCursor` 为主（SPI 保留兼容），恢复默认在 SPI 失败时改为把 `LoadCursor(NULL, OCR)` 的默认光标逐一写回槽位。
4. **有些窗口用"窗口自有光标"**（不受系统槽位影响）：守护比对时会看到"永远不匹配"的假阳性，因此重应用后会复核一次，仍不匹配则进入 60 秒冷却，避免空转。

### Windows 11 还原问题

Windows 11 会话中途可能把系统光标槽位重置为经典样式（微软已承认的已知问题，主题切换/部分更新会触发），注册表里的自定义方案并不受影响。本程序的守护进程专门处理这种情况：检测到槽位内容被还原后自动重新应用，`%APPDATA%\CursorStudio\watchdog.log` 会记录事件。

## 小狗汪汪方案（角色光标管线）

"小狗汪汪"由 `tools/DogCursorGen.cs` 从一张 Q 版小狗角色设定图（`tools/dog/sheet.png`）自动生成，走完整的"设定图 → 主姿态 → 透明化 → 缩放 → Cursor"管线：

1. 按归一化坐标从设定图裁出各姿态
2. 洪水填充从边缘抠掉米色背景与投影（小狗有完整深色轮廓线，填充不会漏进体内）
3. 只保留最大不透明连通域（自动去掉 zZ、问号、爱心等漂浮装饰）
4. 1px 羽化边缘 → 绘制到 48×48 画布 → 按角色热点生成 .cur

> **关于光标尺寸**：Windows 会把所有光标统一渲染为系统指针尺寸（150% DPI 下为 48 物理像素，实测 32/48/64/72/96px 文件全部被归一化到 48），文件尺寸无法突破。
> 因此"正常选择"采用 **1.5 倍绘制 + 顶部锚定裁切**构图：头部充满整个光标、耳朵完整，牺牲下部身体；
> 头像类角色（忙碌/后台运行/链接选择/帮助）若放大 1.5 倍会切掉耳朵，保持完整头像构图。
> 若希望光标整体更大，可在 设置 → 辅助功能 → 鼠标指针 → 大小 调整系统指针尺寸，所有方案会同步变大。

角色映射：

| 光标角色 | 素材 | 热点 |
| --- | --- | --- |
| 正常选择 Arrow | 正面坐姿（1.5 倍头部构图） | 头部右上角高光标记 |
| 忙碌 Wait | 睡觉表情 | 头部右上角高光标记 |
| 后台运行 AppStarting | 好奇表情头像 | 头部右上角高光标记 |
| 链接选择 Hand | 开心吐舌头像 | 头部右上角高光标记 |
| 帮助选择 Help | 呆萌表情头像 | 头部右上角高光标记 |
| 文本/调整大小等 9 个功能角色 | 用设定图配色（蜂蜜棕/奶油/腮红粉）重绘经典形状 | 形状中心 |

每个角色光标的右上角都绘制了"白核 + 深棕环 + 柔光"的**点击区域标记**，热点即标记中心。

重新生成：`rm cursors/manifest.txt && tools\GenCursors.exe . && tools\DogCursorGen.exe .`（顺序不能反，DogCursorGen 会覆盖前 5 个角色）。

## 构建方法

零依赖，任何 Windows 10/11 自带工具链即可：

```
build.cmd
```

流程：`tools\GenCursors.cs`（GDI+ 抗锯齿绘制）→ 生成 `cursors\` 下全部 .cur/.ani 资源 → 以 `/res:` 内嵌 76 个资源 → `csc.exe` 编译出 `dist\CursorStudio.exe`（约 1.3 MB）。修改方案配色只需改 `GenCursors.cs` 顶部的 `Schemes` 表。

## 数据位置

| 路径 | 内容 |
| --- | --- |
| `%APPDATA%\CursorStudio\Schemes\` | 内置方案资源（首次运行从 exe 内嵌资源解压） |
| `%APPDATA%\CursorStudio\Custom\` | 上传图片转换出的 .cur |
| `%APPDATA%\CursorStudio\cursor_backup.txt` | 首次运行前光标注册表备份（"恢复默认"依据） |
| `%APPDATA%\CursorStudio\settings.ini` | 当前方案 / 各开关 |
| `%APPDATA%\CursorStudio\watchdog.log` | 防还原守护事件 |

## 已知限制与注意事项

- **退出行为**：默认勾选"退出程序时恢复系统默认光标"。若希望退出后光标保持自定义，取消勾选即可（重新登录时仍会按注册表自动应用）。
- **程序崩溃不会还原光标**，但注册表方案仍在，重新打开程序或重新登录即可恢复一致状态。
- **Help 角色**没有 OCR 槽位、只能由 `SPI_SETCURSORS` 装载；本机 SPI 失效时该角色沿用系统默认（使用频率极低，无实际影响）。
- 光标渲染尺寸由系统统一决定（150% DPI 下为 48 物理像素），文件尺寸不会改变显示大小；小狗 Arrow 通过 1.5 倍裁切构图让主体显著变大。
- 全屏独占游戏、部分远程桌面会话可能不显示系统级替换的光标。
- 若以管理员身份运行本程序，写入的注册表属于管理员账户；请以普通权限运行（默认即是）。

## 目录结构

```
build.cmd              一键构建
dist\CursorStudio.exe  成品
src\
  CursorCore.cs        核心引擎（P/Invoke、注册表、守护、PNG→CUR、selftest）
  MainForm.cs          主界面 + 托盘
  Program.cs           入口（GUI / CLI 双模式）
  app.manifest         DPI 感知 + 不提权
tools\
  GenCursors.cs        光标资源生成器（6 方案 × 14 角色 + app.ico）
  DogCursorGen.cs      小狗设定图 → 光标管线（抠图/透明化/缩放）
  compile_dog.cmd      编译 DogCursorGen（联动 src\CursorCore.cs）
  inspect_ani.ps1      解析 .ani 结构（开发期验证用）
  dog\sheet.png        小狗角色设定图（抠图源）
cursors\               生成出的资源（构建时内嵌进 exe）
```

## 许可证

[MIT](LICENSE) © 2026 skk

## 参考来源

- [SetSystemCursor (Microsoft Learn)](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setsystemcursor) —— OCR_* 槽位常量、"传入句柄会被销毁"的行为
- [SystemParametersInfo (Microsoft Learn)](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfoa) —— SPI_SETCURSORS / SPIF_* 用法
- GitHub 同类实现的调研（[CursorUpdater.cs](https://github.com/Ixars/ransomdoors/blob/main/CursorUpdater.cs) 等验证了相同的 P/Invoke 模式；[WormsCursor](https://github.com/dawidope/WormsCursor) 的"还原脚本"思路演化为常驻守护）
- [微软官方对 Win11 光标被还原问题的确认](https://learn.microsoft.com/en-gb/answers/questions/5988803/custom-windows-cursors-reverting-to-the-system-def) —— 守护机制的必要性依据
- .ani 帧延迟单位（jiffy = 1/60 秒）通过解析 Windows 自带 `aero_busy.ani`（18 帧、jifRate=3）实测确认
