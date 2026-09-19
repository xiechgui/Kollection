# 拾藏 v0.1 — 本地收藏管理

C# / ASP.NET Core (.NET 10) + SQLite + 原生 Web 前端。无需 Visual Studio、Node.js 或独立数据库服务。面向 Windows x64 的初版。

## 用 GitHub Actions 编译

1. 新建或使用你的 GitHub 仓库。
2. 把本目录中的文件提交到仓库根目录，**包括 `.github` 和 `.vscode` 目录**。仓库根目录应直接看到 `src`、`tests`、`README.md`。
3. 推送后打开 **Actions → Build Windows**。也可以点 **Run workflow** 手动触发。
4. 构建完成后，在该次运行的 **Artifacts** 下载 `Shicang-win-x64`。
5. 解压下载文件；如果里面还有 `Shicang-win-x64.zip`，再次解压。
6. 进入程序目录，双击 `Start.cmd`。浏览器自动打开 `http://127.0.0.1:5278`。
7. 保持控制台窗口开启。关闭控制台或按 Ctrl+C 停止后端。

产物自带 .NET 运行时，运行机器不必安装 SDK。此工作流只上传构建产物，不自动发布 Release。源码包没有附带任何真实用户数据库。

## 本地构建和 Debug

安装 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，可选安装 VS Code 和 C# Dev Kit。无需完整 Visual Studio。

Windows 双击 `Start-Dev.cmd`；也可以执行：

```powershell
cd src/Collection.Web
dotnet run -- --open
```

VS Code 打开仓库根目录，按 F5 选择“拾藏 C# 后端”。前端可以在浏览器开发者工具调试；修改静态文件后刷新浏览器即可。

手动构建与发布：

```powershell
dotnet build src/Collection.Web/Collection.Web.csproj -c Release
dotnet publish src/Collection.Web/Collection.Web.csproj -c Release -r win-x64 --self-contained true -o artifacts/Shicang-win-x64
```

将 `scripts/Start.cmd` 复制到发布目录后双击运行，或执行 `Collection.Web.exe --open`。

## 已实现

- 新建、修改、删除通用 Item；没有文件标签的对象不当成文件。
- 标签创建与编辑、单父标签继承、继承循环校验。
- 标签也作为属性名；每个标签定义其属性值类型与关联字段。
- 文本、数字、单 Item 引用、有序 Item 数组。
- 引用跳转、反向引用查看、引用排序和移除。
- 被引用的对象禁止直接删除；正式对象不能引用暂存对象。
- 搜索名称、分类标签、普通属性值与引用对象名；标签筛选含子标签。
- 卡片/列表视图，每次显示 60 项并可加载更多。
- 图片预览、浏览器原生视频预览、Windows 资源管理器定位。
- 按本机绝对路径导入一个文件或扫描文件夹，支持子文件夹。
- 根据扩展名推荐文件/图片/视频/文字标签，自动读取名称、路径和大小。
- 导入对象整体暂存；可修改或移除候选标签和属性，再确认入库。
- 同路径重复导入跳过，放弃暂存或删除记录不删除原文件。
- SQLite 持久化、整库 JSON 导出、版本号冲突保护。
- GitHub Actions 编译、真实 API 集成测试、Windows 自带运行时 ZIP 打包。

## 使用建议

1. 新建“演员”对象，保存。
2. 新建“电影”对象，在演员属性中选择刚才的对象；电影此时没有本地文件身份。
3. 导入一个本地视频路径，在暂存区选择候选对象。
4. 调整标签和属性，点击“确认入库”。
5. 返回电影，在“视频版本”属性中选择视频文件对象。
6. 点击引用对象即可查看视频和文件信息。

“清空属性”按钮移除已有值；如果字段由分类标签定义，它仍会作为空输入项显示。空白输入表示未填写。Item 数组可用 ↑ 调整顺序。

文件导入框填写后端所在电脑的实际路径，例如 `D:\Movies`。这是本机路径引用，不是浏览器上传；不会搬运原文件。

## 数据位置和备份

默认：`%LOCALAPPDATA%\Shicang\collection.db`。升级程序不会清除这份数据。

可通过环境变量配置：

```powershell
$env:COLLECTION_DATA = 'D:\ShicangData'
$env:COLLECTION_PORT = '5278'
```

备份时关闭程序，再复制整个数据目录。JSON 导出用于查看或后续迁移，初版尚无 JSON 恢复界面。媒体文件保留原位置，需要自行备份；文件搬家后编辑“路径”属性即可。

## 当前边界

- 初版最多 5000 个 Item（含暂存）、500 个标签；每批导入最多 500 个新对象，重复导入同目录会跳过已有路径继续处理。
- 数据以一个 JSON 文档在 SQLite 事务中保存，每次操作加载文档；这是为了简化可运行原型。尚未实现大规模按属性索引、后台扫描队列、缩略图缓存。不要把本版当成十万级媒体库。
- 文件扫描在请求中处理；大目录可能等待较久，跳过系统目录、重解析点和不可访问目录。尚无实时进度、取消或目录监听。
- 图片直接按原文件读取，没有自动生成缩略图；预览效果与浏览器支持的格式有关。
- 视频播放使用浏览器原生控件；MKV、AVI、HEVC 等不保证可播，可定位后用外部播放器打开。不执行任意导入文件。
- 浏览器扩展、网站规则、视频下载、自动刮削、AI 识别、已有对象的字段级自动补充暂未实现。普通网页与摘抄可手动新建。
- 暂存审核以新对象为单位；批量确认、候选间互相引用尚未实现。
- 自定义标签可新建、改名和编辑，但尚不提供删除界面；修改属性类型若与已有值不兼容会拒绝保存。
- 首次运行为空收藏库，只有预置标签。没有伪装成用户内容的示例数据。
- 仅监听本机回环地址；不提供局域网共享、账户或远程访问。

## 项目结构

- `src/Collection.Web/Program.cs`：HTTP 接口、静态资源与本地访问约束。
- `Models.cs`：Item、Tag、属性值模型。
- `LibraryStore.cs`：SQLite 事务、乐观版本锁、模型校验、预置标签。
- `FileImporter.cs`：本地文件识别与暂存。
- `wwwroot/`：独立 CSS、JavaScript 和 HTML，无前端构建依赖。
- `.github/workflows/build.yml`：Windows 构建与打包。
- `.vscode/`：本地 F5 调试配置。
- `tests/smoke.py`：使用临时数据库启动真实进程测试，需 Python 3。

```powershell
dotnet build src/Collection.Web/Collection.Web.csproj -c Release
python tests/smoke.py
```

测试不读取或修改正式收藏库。GitHub Actions 会执行相同测试。运行验证记录见 `VALIDATION.md`。
