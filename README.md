# PdnodeVote

一个基于 **.NET 10 Blazor WebAssembly（Interactive Auto）+ ASP.NET Core Hosted** 的社区投票平台。

## 功能概览

| 模块 | 说明 |
|---|---|
| 投票 | 单选 / 多选、截止时间、`AlwaysPublic` 与「投票后可见」结果策略 |
| 板块 | 多级板块树、发帖权限（任何人 / 仅版主 / 仅管理员）、板块级版主映射 |
| 审核 | 新账号与低产出用户自动进入审核队列；批准 / 退回修改 / 删除 / 归档 |
| 评论 | 两级嵌套、点赞、置顶、按账号信誉自动进审核 |
| 社区 | 标签、板块关注、举报与处理、用户等级、站内通知 |
| 权限 | Admin / SuperModerator / Moderator / User，含固定 UUID 的主管理员（防锁死） |

## 技术栈

- .NET 10（`net10.0`）、Blazor WebAssembly + Server 混合渲染
- EF Core 10 + **SQLite**
- MudBlazor 9 + Bootstrap 5
- SignalR（实时投票/评论/通知）
- xUnit（146 条测试）

## 本地运行

```bash
dotnet run --project PdnodeVote.csproj
```

首次启动需要一个管理员账号。**没有默认密码**——按环境二选一：

- **开发环境**：未配置密码时会生成一次性随机密码并打印在日志里
- **生产环境**：必须显式提供，否则拒绝启动：

```bash
export BootstrapAdmin__Password='YourStrongPassword123'
# 或
export PDNODEVOTE_ADMIN_PASSWORD='YourStrongPassword123'
```

> 管理员邮箱固定为 `admin@vote.com`，其用户 ID 是固定的 `00000000-0000-0000-0000-000000000000`，
> 用于保证主管理员不可被封禁、不会因改名而失去权限。

## Docker

```bash
# 构建
docker build -t pdnodevote:local .

# 运行
docker run -d --name pdnodevote \
  -p 8080:8080 \
  -e BootstrapAdmin__Password='YourStrongPassword123' \
  -v pdnodevote-data:/app/Data \
  -v pdnodevote-uploads:/app/wwwroot/uploads \
  pdnodevote:local
```

访问 http://localhost:8080

### 必须挂载的两个卷

| 卷 | 路径 | 原因 |
|---|---|---|
| `pdnodevote-data` | `/app/Data` | SQLite 数据库。连接串 `Data/app.db` 相对于内容根（`/app`）解析 |
| `pdnodevote-uploads` | `/app/wwwroot/uploads` | 用户上传的图片。由 `UseStaticFiles` 从这里提供 |

**不挂卷的话，容器重建即丢数据。**

### 生产环境注意事项

- **反向代理**：应用默认**不信任** `X-Forwarded-For`。若部署在 Nginx/CDN 之后，必须显式配置可信代理，否则所有客户端会共用代理 IP（导致游客投票被误判为重复）：

  ```bash
  -e ForwardedHeaders__KnownProxies__0=10.0.0.1
  # 或网段：
  -e ForwardedHeaders__KnownIPNetworks__0=10.0.0.0/8
  ```

- **HTTPS**：容器只监听 HTTP（8080），请在反向代理层终止 TLS。
- **邮件**：默认 `SmtpSettings:Host` 为空，此时邮件不会发送（也不会写入日志正文）。需要邮件功能请配置完整 SMTP 参数。

## 测试

```bash
dotnet test PdnodeVote.slnx
```

## 配置项

| 配置键 | 用途 |
|---|---|
| `ConnectionStrings:DefaultConnection` | SQLite 连接串（相对路径按内容根解析） |
| `BootstrapAdmin:Password` | **首次**创建管理员用的密码；生产环境未提供则拒绝启动 |
| `ForwardedHeaders:KnownProxies` / `KnownIPNetworks` | 可信反向代理。留空表示不信任任何转发头 |
| `SmtpSettings:*` | 邮件发送；Host 为空则不发信 |
| `PublicBaseUrl` | 生成 QR 码等对外链接时使用的公共地址（未配置则回退请求 Host） |

## 许可

未指定许可证。
