# 自言卡 · MyQuoteCard（个人修改版）

本项目 fork 自 [oocreator/MyQuoteCard](https://github.com/oocreator/MyQuoteCard)，遵循原项目 MIT License。
原作者版权归 oocreator 所有，本版本仅为个人自用二次修改版本，非官方版本。

> 原项目介绍：一张每天随机显示你自己文字的 Windows 桌面卡片，支持完全自定义。

## 特性

### 新增功能
1. 独立启动器：多实例管理（最多 9 张卡片）、多显示器位置记忆、开机自启
2. 窗口置顶与点击穿透，内置 10 秒自动巡检自愈，修复重启后穿透状态 UI 与实际窗口不一致的问题
3. 启动器单实例限制，防止重复打开
4. 图标分离：启动器为黄色便签图标（QuoteCardLauncher.exe），主程序为橘色图标（MyQuoteCard.exe）

### 界面调整
1. 重写内容右键菜单渲染，改用原生 Win32 深色控件，修复菜单文字丢失与对齐粗糙问题
2. 编辑内容弹窗重做深色主题，统一深色 UI 风格
3. 列表列宽支持自定义记忆，默认列宽 65，总宽 720

### Bug 修复
1. 修复多卡片穿透开关状态互相串扰的问题
2. 修复菜单点击延迟、闪烁等交互问题

## 更新日志
- V37：首个正式定制改版，包含上面全部新增功能、界面调整与 Bug 修复
- V37（重新打包）：启动器窗口标题显示版本号；卡片右键菜单新增「关于」，可直接看到当前版本；两个 exe 的文件属性里也写入了 3.7.0.0

## 快速开始
1. 前往 Releases 页面下载最新的 `MyQuoteCard-MOD-V37.zip`
2. 解压到任意非中文路径的文件夹（不建议放在 C 盘系统目录）
3. 运行 `QuoteCardLauncher.exe` 管理卡片；`MyQuoteCard.exe` 由启动器拉起，不建议单独双击

> 首次运行若被 Windows SmartScreen 拦截：点「更多信息 → 仍要运行」，本程序无数字签名。

## 数据与配置
卡片数据保存在 `%LOCALAPPDATA%\MyQuoteCard\`：

- `state.ini`：卡片外观与状态设置
- `quotes.txt`：卡片上显示的文字内容
- 第 2 张及以后的卡片使用 `instance2` … `instance9` 子目录，最多同时运行 9 张

> 升级时只需替换程序文件，配置在 AppData 内不受影响；想彻底重置就删掉该目录。

## 编译与运行环境
- 编译：`build.bat` 调用系统自带的 .NET Framework `csc.exe` 直接编译，无需安装 SDK
- 运行：Win10 / Win11 自带 .NET Framework 4.8，无需额外安装运行时
- 产物：`dist\MyQuoteCard.exe`、`dist\QuoteCardLauncher.exe`

## 维护说明
- 本版本为二次定制修改，仅个人使用，不商用；作者不对使用后果负责
- 上游原作者后续更新会优先评估，按需增量合并，不保证持续同步
- 编译后的绿色发行包发布在 Releases 页面

## License
本项目沿用上游 MIT 协议，详见根目录 LICENSE 文件。