# Lodestone · 艾欧泽亚档案

Windows x64 角色档案客户端，使用 .NET MAUI、HybridWebView、Vue 和 WebGL2。W0 提供本地演示角色、装备候选、全身和面部预览，以及 PNG 导出。

## 构建

需要 Windows 10 2004 或更新版本、WebView2 Evergreen、Node.js 24 和 .NET SDK 10.0.402。MAUI Windows 工作负载集固定在 `global.json`，宿主使用 MAUI 10.0.110。Windows SDK 构建工具随 NuGet 依赖还原。

```powershell
dotnet workload install maui-windows
.\scripts\build-windows.ps1
```

运行 `src\Lodestone.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\Lodestone.App.exe`。使用 `-Publish` 可生成自包含的应用目录，发布文件位于同一输出树下的 `publish`。启动示例无需网络，构建需要从 npm 和 NuGet 恢复锁定依赖。

## 核对 Windows 预览

1. 启动后点击「进入演示」，选择「档案人偶」。
2. 打开上衣详情，切换同模候选。
3. 点击「3D 预览」，拖动角色并切换全身和面部，确认场景和贴图已加载。
4. 点击「导出 PNG」，检查 WebView2 下载的 880 × 1200 PNG。

前端、样式和渲染器来自随开发任务提供的参考源码。示例人偶、装备条目、图标及背景均为合成素材。Windows 字体使用系统回退，场景和贴图通过 C# 处理的 HybridWebView 虚拟资源读取。运行时无需启动额外服务。
