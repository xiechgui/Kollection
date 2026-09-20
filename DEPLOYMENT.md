# 云端部署准备

目标：单个 C# 实例 + 同一持久化磁盘中的 SQLite 和上传文件。当前只有代码与配置准备完成，尚未创建实际服务或网址。

## Railway 配置

1. 从 xiechgui/Kollection 仓库创建服务，使用根目录 Dockerfile。
2. 创建持久化 Volume，挂载到 `/data`。必须在首次使用前完成，容器自身文件系统不能用于长期保存收藏。
3. 生成公开域名，平台负责 HTTPS。程序读取 `RAILWAY_PUBLIC_DOMAIN`；使用自定义域名时设置 `COLLECTION_PUBLIC_URL=https://实际域名`，不带子路径。
4. 将至少 32 字符的随机访问口令存入秘密变量 `COLLECTION_ACCESS_KEY`，不要提交到 GitHub。
5. 保持单副本。程序读取平台 `PORT`；健康检查路径为 `/health`。
6. 部署后检查未登录无法读取收藏、输入口令可登录、上传先暂存、重启后数据保留。然后交付真实 HTTPS 地址。

Docker 默认设置 `COLLECTION_CLOUD=1`、`COLLECTION_DATA=/data`。云端模式强制所有请求登录，不保留回环登录豁免；会话 Cookie 使用 Secure、HttpOnly 和 SameSite=Strict。不信任任意转发头，按配置的 HTTPS 源校验 Origin 与 Host。

云端数据独立于 Windows 本地收藏库，不自动同步电脑文件；使用手机上传或手动新建。云端不提供服务器文件扫描、资源管理器定位，媒体接口仅提供上传目录中的文件。关闭/重启实例会使登录会话失效，访问口令仍由秘密变量保存。

## 验证范围

代码构建、云端鉴权与上传测试可在云端执行；实际 Docker 构建、Volume 挂载、外网 HTTPS 和费用需在连接 Railway 后验证。未连接时不能声称已上线。

参考：
- https://docs.railway.com/builds/dockerfiles
- https://docs.railway.com/variables/reference
- https://docs.railway.com/volumes
