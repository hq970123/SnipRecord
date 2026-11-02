# 截图录屏 · SnipRecord（WidBar 小组件）

放在任务栏上的 WidBar 小组件：一键截图、矩形/窗口录屏、屏幕取色。

> 当前为脚手架阶段，功能尚未实现。

## 安装
1. 到 Releases 下载对应架构的 `SnipRecord_x64.zip`（大多数电脑）或 `SnipRecord_ARM64.zip`。
2. 解压后双击 `Install.bat`（会请求管理员权限以信任自签名证书）。
3. 打开 WidBar，在控件目录里找到「截图录屏」，拖到任务栏。

## 构建
推送到 `main` 后由 GitHub Actions（`.github/workflows/build.yml`）构建 x64 / ARM64 的 MSIX 并发布 Release。
本地：Visual Studio 2022+，.NET 10，打开 `SnipRecord.sln`，以 `SnipRecord (Package)` 为启动项目。
