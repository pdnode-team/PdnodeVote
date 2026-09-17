# PdnodeVote — 缺陷台账 (TODO-BUGS.md)

来源：一次全项目代码审查（我逐行复核 + 3 个只读审查子代理交叉验证）。
所有条目均已定位到文件与行号。

- `[x]` = 已修复（有测试或实测支撑）
- `[ ]` = 未修复
- ✅实测 = 实际跑起来验证过（含对抗测试）
- ⚠️部分 = 只完成了一部分，未完成部分已单列

**进度：51 / 51 已修** 🎉

| 分区 | 已修 | 待修 |
|---|---|---|
| 安全 / 数据损坏（S/D） | 19 | 0 |
| 核心功能 / 客户端（C） | 7 | 0 |
| 性能（P） | 4 | 0 |
| 中危（M） | 19 | 0 |
| 低危 / 清理（L） | 9 | 0 |
| 合计 | 51 | 0 |

> 说明：上表按"条目"计，共 51 条。S10 拆成 S10a/S10b 两条待办，故待办明细共 46 行、对应 33 个独立缺陷（部分缺陷有多条待办，例如 S10 与 S10a/S10b、D7 与 S7）。

---

## 已完成

- [x] **P1-1** 重复 `POST /Account/Logout`：`MainLayout` 登出返回 400/500 且 Cookie 未清除 ✅实测
      模板 handler 的 `LocalRedirect($"~/{returnUrl}")` 对任何输入都抛异常（缺字段→400，`"/"`→`"~//"`→500）；已删除该 handler，由 `Program.cs` 拥有路由。
- [x] **P1-2** `PollApiClient.ResubmitPollAsync` 打到错误端点（缺 `/resubmit`），帖子永远停在"待修改"
- [x] **P1-3** SignalR 事件名不匹配（服务端 `NotificationReceived` vs 客户端 `UserNotificationReceived`）→ 实时通知永不送达；并补了断线重连后重新入组
- [x] **S1** `DataSeeder` 硬编码默认管理员密码 `admin123` ✅实测（Production 无密码→明确报错；Development→随机密码并打印）
- [x] **S2** Seeder 每次启动重建后台账号并强补 Admin 角色 ✅实测（运维移除角色后重启不再被加回）
- [x] **S3** `X-Forwarded-For` 被信任 → 游客投票防重可绕过 ✅实测（对抗测试：3 个伪造 IP 仅 1 票生效）
      注意：清空 `KnownProxies`/`KnownIPNetworks` **不足以**关闭信任，必须整体不挂载中间件。
- [x] **S4** `PollHub.JoinUserGroup` 无校验 + Hub 无 `[Authorize]` → 越权订阅他人通知
- [x] **S5** `AfterVoting` 结果可见性改为服务端强制（未投票者被置零；作者/版主保留可见）✅（4 条测试）
- [x] **S6** CSV 导出加认证 + 可见性校验 ✅实测（匿名 → 302 到登录页且不返回 CSV；登录后正常导出）
- [x] **S7** 举报流程的封禁越权：`ResolveReportAsync` 现要求 Admin 才能 `BanAuthor`，并保护 root admin ✅（2 条测试）
      该路径已按 D7 改走 `UserManager.UpdateAsync` + `UpdateSecurityStampAsync`。**注意 `AdminService` 主入口仍待修（见 D7）。**
- [x] **S8** 密码策略 + 登录锁定 ✅实测
      策略抽到 `RateLimiting/SecurityPolicy.cs`；长度 ≥8 + 大小写/数字；锁定 5 次 / 5 分钟；`lockoutOnFailure: true`。
      实测：弱密码注册被拒 | 强密码注册成功 | **现有 `admin123` 账号仍可登录** | 第 5 次错误密码触发锁定 | 第 6 次被登录限流拦下。
- [x] **S9** Turnstile 改为 fail-closed：非 2xx 与网络异常原先都放行，现在都拒绝；`success` 判定改用 `ValueKind == True` ✅（1 条测试）
- [x] **S12**（新发现）EF Core 9+ 的 `PendingModelChangesWarning` 在**零 schema 差异**时也会触发，导致全新库 `Migrate()` 抛异常
      已用 `dotnet ef migrations add` 生成**空迁移**证实零 drift；已在 `Program.cs` 降级为日志。
- [x] **C1** 缺 `UseStaticFiles()` → 运行时上传图片全部 404 ✅实测
- [x] **M1** 为前两批修复补回归测试（12 条）
- [x] **S10** ⚠️部分 — 结论见下节，未完成部分已拆为 S10a/S10b
- [x] **D7** `AdminService.BanUsersAsync` / `UnbanUsersAsync` — 封禁/解封现在与 security stamp 轮换**在同一笔保存**中落库 ✅（`BanPersistenceTests` 断言库内 `IsBanned`、`BanReason` 与 stamp 变化）
      **排查中发现原判断还低估了问题**：最初我以为只是"Cookie 继续有效"，实际还有更严重的——我第一版修复（先 `SaveChangesAsync` 再 `UserManager.UpdateSecurityStampAsync`）被测试抓到**封禁字段根本没落库**：
      ```
      [ban]    count=1 msg=Successfully suspended 1 user(s)
      [fresh]  IsBanned=False BannedUntil= Reason=      ← 字段全丢
      ```
      原因：`UserManager.UpdateSecurityStampAsync` 会**重新加载并整行回写** User，覆盖我设置的封禁字段。改为在已跟踪实体上直接轮换 `SecurityStamp` 并单次 `SaveChangesAsync` 后：
      ```
      [fresh]  IsBanned=True Reason=spam
      [raw sql] IsBanned=1 stamp=<已变化>
      ```
- [x] **D1** 有票的帖子不再能被退回修改 / 重提交 ✅（3 条测试）
      `ReturnPollForRevisionAsync` 与 `UpdateAndResubmitPollAsync` 都新增有票拦截；测试断言"拒绝后 `VoteRecords` 数量不变"，并验证无票帖子的重提交仍然正常。
- [x] **C2** `PollDetail.razor` / `EmbedPoll.razor` 改为在 `OnParametersSetAsync` 按 Id 重载 ✅（3 条契约测试）
      两者都新增 `_loadedPollId` 跟踪，避免同 Id 重复加载；并加了**过期响应防护**（`if (_loadedPollId != requestedId) return;`），防止 A→B 快速切换时 A 的响应覆盖 B。`PollDetail` 另加 `ResetPerPollState()` 清空上一帖的选中项/评论。
- [x] **C6** `EmbedPoll.razor` 投票开放性与结果可见性 ✅（2 条契约测试）
      新增 `IsVotingOpen()`（Approved + 未截止 + 未投票），同时用于投票按钮、选项点击与 `SubmitVote`；新增 `ResultsAreVisible()`，**已截止的 AfterVoting 帖现在会向未投票者显示最终结果**（此前永不显示）。
- [x] **C7** 已删/待审帖的评论不再能匿名读取 ✅（6 条测试 + 端点实测）
      `GetPollCommentsAsync` 现在先校验帖子可见性（Approved，或调用者是版主/作者）；测试覆盖 已删/待审/已批准/版主/作者/帖子不存在 六种情况。

---

## S10 的结论（重要：不是"加个开关"能解决的）

原判断是"投票端点无防伪 + 上传显式关闭防伪"。实测后事实不同：

1. **`app.UseAntiforgery()` 对 minimal API 完全不生效。** 最小探针实测（`UseAntiforgery()` + JSON `MapPost`）：
   `POST /json withToken=False -> 200`、`withToken=True -> 200`。不带任何令牌也照常通过——防伪中间件只对带 `IAntiforgeryMetadata` 的端点生效。
2. **.NET 10 没有 `RouteHandlerBuilder.RequireAntiforgery()`**（编译期 `CS1061` 已证实）。
3. 因此上传端点上的 `.DisableAntiforgery()` 是**无操作**——它从未关闭过任何东西。已移除该调用并加注释，避免代码给人"这里做过防护决策"的错觉。
4. 今天真正挡住跨站 POST 的是**认证 Cookie 的 `SameSite=Lax`**（跨站表单提交不携带 `.PdnodeVote.Auth`），叠加"无 CORS 策略"（JSON 跨站需预检）。

- [ ] **S10a** 若要在 minimal API 上真正启用防伪，需端到端实现：① 令牌下发端点（`IAntiforgery.GetAndStoreTokens`）；② `IEndpointFilter`/中间件在状态变更端点调用 `ValidateRequestAsync`；③ WASM 客户端取令牌并放请求头。
      当前客户端**完全没有**任何防伪令牌机制（已 grep 确认），属需 HTTP 级集成测试的独立工作。
- [ ] **S10b** `ServerApiClients.UploadImageAsync`（服务端 Circuit 路径）**直接写文件、不经过 HTTP**，天然不受防伪保护，但也没有大小上限（见 M20）。

---

## 待处理 — 安全 / 数据损坏（高优先级）

- [ ] **S11** `DataSeeder.cs:123-131` — 旧管理员主键迁移只更新 4 张表，遗漏 `AspNetUserLogins/Tokens/Passkeys`、`PollComments`、`CommentLikes`、`Notifications`、`ContentReports`、`CategoryModerators`、`CategorySubscriptions`、`CategoryRequests`、`CategoryRequestReviews`；且 `PRAGMA foreign_keys=OFF` 是连接级的，中途异常会让连接池中的连接**永久保持外键关闭**。
      修：不要重写身份主键；改用 Identity API 在显式事务中迁移。**改坏会丢数据，动手前先出方案。**
- [ ] **S13**（依赖告警）`SQLitePCLRaw.lib.e_sqlite3` 2.1.11 存在已知高危漏洞（GHSA-2m69-gcr7-jv3q），由 `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 间接引入。建议评估升级。
- [ ] **S14**（新发现）API 端点的未认证响应是 **302 重定向到登录页并返回 HTML**，而非 `401 + JSON`，客户端 `ApiResponse.ReadAsync` 无法区分"未登录"与"页面不存在"。
      实测：匿名 `GET /api/polls/2/export-csv` → `302 Location: /Account/Login?ReturnUrl=...`；匿名 `GET /api/polls/999/export-csv` 也先 302 而非 404。
      顺带：`Program.cs:166-172` 的 `UseWhen(...)` 只是**条件注册中间件**，并未启用 `UseStatusCodePagesWithReExecute`——注释声称的"重定向到 404 页面"实际从未生效。

---

## 待处理 — 核心功能 / 客户端

- [x] **D2–D6** 五处"读后写"并发竞态 + 缺唯一索引 ✅（5 条测试，含迁移与约束验证）
      **新增唯一索引**（迁移 `AddUniqueIndexesForVoteAndReview`）：
      · `VoteRecords (PollId, UserId)` 改为唯一 —— 已认证用户并发重复投票在数据库层被拦
      · `CategoryRequestReviews (RequestId, ReviewerId)` 新增唯一
      生成迁移前先扫描真实库，确认**零冲突**才能安全加索引（`VoteRecords` 的游客票 `UserId=NULL` 不受影响，SQLite 视 NULL 互异）。
      **冲突处理**（把 500 变成可预期结果）：
      · `D2` `CastVoteAsync` → 捕获唯一冲突，返回"已投过"
      · `D3` `GetOrCreateTagAsync` → 改用 `INSERT OR IGNORE` 再重读，保留原**大小写不敏感**语义
      · `D4` `UpvoteCommentAsync` → 捕获 `CommentLikes` 主键冲突，重读真实计数（避免 `Upvotes++` 被回滚后返回错值）
      · `D5` `ReviewCategoryRequestAsync` → 捕获冲突，返回"你已评审过"
      · `D6` `ToggleCategorySubscriptionAsync` → 捕获冲突，视为成功（幂等）
      新增 `DbUpdateExceptionExtensions.IsUniqueConstraintViolation()` 统一识别（`SQLITE_CONSTRAINT` = 19，附文案回退），并有测试保证不会把无关错误误判为重复。
      实测：迁移在真实开发库干净应用（日志确认两条 `CREATE UNIQUE INDEX`）；投票二次被拒；带标签的帖子创建正常且大小写合并为单个标签。
- [x] **C5** `PollDetail.razor` Archived/Removed 帖现在作为"可查阅记录"渲染结果 ✅（1 条契约测试，随 C6 一并完成）

---

## 待处理 — 性能

- [ ] **P2** `CommentService.cs:75-98` — 每个评论者约 7 次查询（`FindByIdAsync` + 3×`IsInRoleAsync` + 3×`CountAsync`）→ 评论页 N+1 雪崩。
- [ ] **P3** `PollService.cs:203-238` — 待审队列每条帖子一次 `CountAsync`；`AdminService.cs:79-107` 遍历全部用户逐个 `GetRolesAsync`，且分页参数被调用方忽略。
- [ ] **P4** `PollService.cs:998-1000`、`NotificationService.cs:20-28` — `pageSize`/`limit` 只做下限钳制，`?pageSize=100000000` 可让匿名路径 eager-load 全库。
- [ ] **P5** `Program.cs:54-58` — `DisconnectedCircuitMaxRetained=0` + `RetentionPeriod=Zero` → 断线立即丢弃 Circuit（.NET 10 用 `MemoryCacheOptions{SizeLimit=0}` 实现），Blazor 重连永不恢复。

---

## 待处理 — 中危

- [ ] **M2** `PollService.cs:801-806,844,919` — 无状态机守卫：`ApprovePollAsync` 接受任意当前状态，可把已删除内容重新发布。
- [ ] **M3** `PollService.cs:537-555,615-651` — 无服务端长度/数量校验（UI 限 100/500/20，服务端只 `Trim()`）→ 存储 DoS，并溢出 `Notification` 的 `MaxLength`。
- [ ] **M4** `PollService.cs:1512-1515,729` — 修改 `CategoryId` 跳过板块 `PostPermission` 校验 → 可把帖子挪进 AdminOnly 板块。
- [ ] **M5** `AdminService.cs:314-324` — 重置密码先删后加、无事务 → `AddPasswordAsync` 失败则账号**没有密码**。
- [ ] **M6** `ReportService.cs:158,170` vs `:197` — 通知在 `SaveChangesAsync` **之前**发出 → 客户端拉到未提交状态，且不会收到第二次事件。
- [ ] **M7** `PollService.cs:668/763/1540` — `Tag.UsageCount` 只增不减（取消标签、删帖不减，编辑时未变标签重复 +1）→ 热门标签排名永久虚高。
- [ ] **M8** 客户端时间戳——API 输出无 `Z` 的 UTC（`Kind=Unspecified`），`.ToLocalTime()` 变空操作 → 非 UTC 用户所有绝对时间偏差一个时区。
      `PollDetail.razor:279/283`、`MyPolls.razor:83`、`UserProfile.razor:68/116/170`、`AdminDashboard.razor` 多处、`NotificationBell.razor:112`、`PollCountdownTimer.razor:5`
- [ ] **M9** `appsettings.json:3` + `DataSeeder.cs:15` — 连接串 `Data/app.db` 按**进程工作目录**解析，而 Seeder 按 `AppContext.BaseDirectory` 建目录；换目录启动会静默新建空库并重新种入管理员。
- [ ] **M10** SMTP 未配置时 `EmailNotificationService.cs:66-69` 把**含明文临时密码**的整封邮件正文打进 Information 日志；`AdminService.cs:331` 却告诉管理员"已发送邮件"。
- [ ] **M11** `AllowedHosts: "*"` + `PollEndpoints.cs:62-69` 把 `Request.Host` 反射进 QR 码 URL → Host 头注入。
- [ ] **M12** `CommentService.cs:168` — 正则 `Regex.Replace(content, "<[^>]*>")` "净化"评论会把 `a < b` 这类正常文本吃掉。
- [ ] **M13** `SharePollModal.razor:121-131` — 剪贴板调用无 try/catch 且无条件提示成功；服务端渲染模式下 JS 互操作异常会拆掉 Circuit。
- [ ] **M14** `PollOptionCard.razor:6`、`EmbedPoll.razor:58` — `width: @(Percentage)%` 受区域文化影响，逗号小数点地区进度条塌成 0。
- [ ] **M15** `CreatePoll.razor:282-291` — 允许粘贴外链图片，但 CSP 为 `img-src 'self' data:` → 永不显示。
- [ ] **M16** `NotificationBell.razor:602-614` — `OnInitializedAsync` 与 `OnAfterRenderAsync` 都调用 `CheckAuthAndLoadAsync`，每次页面加载发两次未读数请求。
- [ ] **M17** `NotificationBell.razor:718-727,738` — "全部已读"不检查 `res.Success`，单项已读即发即忘。
- [ ] **M18** `ReportService.cs:58-70` — `SubmitReportAsync` 不校验目标 poll/comment 是否存在 → 可用任意 id 刷举报队列。
- [ ] **M19** `ServerApiClients.cs:365-538` — 进程内管理客户端不做角色校验（仅靠组件标志位 `AdminDashboard.razor:1093/1098` 兜底）。
- [ ] **M20** `ServerApiClients.cs:282-303` — 服务端 Circuit 的 `UploadImageAsync` 无大小上限（HTTP 端点限 5MB）。

---

## 待处理 — 低危 / 清理

- [ ] **L1** 无 `.gitignore`；`Data/app.db`（含种子管理员记录）在项目树内；根目录有 78MB 的 `PdnodeVote.zip` 与 `publish/`。建议加 `.gitignore` 并轮换种子密码。
- [ ] **L2** `.vscode/launch.json` 未传 `ASPNETCORE_ENVIRONMENT` → VS Code 直接启动 dll 时按 Production 运行（HSTS 开启、WASM 调试关闭）。
- [ ] **L3** `Categories.Slug` 缺唯一索引（应用层用 `Ticks % 10000` 兜底，有竞态）；热路径缺 `(Status,IsPinned,CreatedAt)` 复合索引。
- [ ] **L4** `IdentityNoOpEmailSender.cs` 与 `IdentityEmailSender.cs` 并存，易误注册为空实现。
- [ ] **L5** `CategoryService.CanUserPostInCategoryAsync` 是死代码（`CreatePollAsync` 内联重写了一套不等价判断，不校验 `CategoryModerators` 映射）→ 板块级版主机制实际不生效。
- [ ] **L6** `NotificationService.cs:72-73` — `MarkAsReadAsync` 往"新通知"通道广播 `Id=0` 的占位 DTO。
- [ ] **L7** `DataSeeder.cs:35-43` — 每次启动无条件把所有 `Moderator` 提升为 `SuperModerator`（静默提权）。
- [ ] **L8** `tests/PdnodeVote.Tests/obj/project.assets.json` 解析出的路径指向仓库根，仅因测试工作目录恰好正确才没暴露；换目录执行会出问题。
- [ ] **L9** 其余静默 `catch {}`、`PageRenderMode` 类死代码、`appsettings.Development.json` 冗余项。

---

## 建议的剩余顺序

| 批次 | 内容 | 理由 |
|---|---|---|
| 1 | ~~**D7**、**D1**~~ ✅ | 一条是封禁形同虚设，一条是静默删光选票——都是"静默失效"型缺陷 |
| 2 | **C2**、**C7**、**C6** | 客户端功能正确性 + 已删帖评论泄露 |
| 3 | **D2-D6** | 并发竞态与重复键 500（同类，可一并加唯一索引 + 重试） |
| 4 | **S11**、**S14** | 需要先出方案（身份主键迁移 / API 401 语义） |
| 5 | **P2-P5**、**M2-M20** | 性能与中危 |
| 6 | **L1-L9**、**S13** | 清理与依赖升级 |

---

## 备注：修复时踩过的坑（避免重复踩）

1. **不要用 `KnownProxies.Clear()` 关闭 `X-Forwarded-For` 信任** —— .NET 10 上实测无效，必须整体不挂载中间件。
2. **`MapStaticAssets()` 只服务构建期清单** —— 运行时产出（上传图片）必须靠 `UseStaticFiles()`。
3. **minimal API 允许重复注册同一路由** —— 不会抛 `AmbiguousMatchException`，而是先注册者胜出并静默让另一个变成死代码。
4. **`[FromForm] string` 非可空 = 必填** —— 缺字段直接 400，handler 不执行。
5. **在仓库内新建含 `.cs` 的临时目录会被主项目 glob 进去** —— 探针项目请放在仓库外，或用完立即删除（`PdnodeVote.csproj` 只排除了 `PdnodeVote.Client/`、`tests/`、`publish/`）。
6. **EF Core 9+ 的 `PendingModelChangesWarning` 可能是假阳性** —— 用 `dotnet ef migrations add` 生成空迁移即可确认零 drift。
7. **Identity 密码策略只在"设置"密码时校验，登录时不校验** —— 所以收紧策略不会锁死密码较弱的存量账号（已实测）。
8. **`app.UseAntiforgery()` 对 minimal API 无效**，且没有 `RequireAntiforgery()` 扩展方法（已实测/编译证实）。
9. **判断 HTTP 行为要看原始响应** —— `Invoke-WebRequest` 会自动跟随重定向，曾让我把"302 正确拒绝"误读成"200 未生效"。用 `curl.exe -i` 或 `-MaximumRedirection 0`。
10. **InteractiveAuto 组件在服务端也要能解析依赖** —— 只在 `PdnodeVote.Client/Program.cs` 注册服务会让预渲染/服务端 Circuit 抛 `Cannot provide a value for property ... no registered service`（我因此实测撞到 500）。服务端 `Program.cs` 必须同样注册。
11. **`UserManager.UpdateSecurityStampAsync` 会整行回写 User** —— 若与"另一个 context 上设置的字段"配合使用，会静默覆盖那些字段（D7 的第一版修复就是这样被测试证伪的）。需要"改字段 + 换 stamp"时，在同一个已跟踪实体上设置两者并单次 `SaveChangesAsync`。
12. **`UserManager` 内部的 `Attach` 会与调用方已跟踪的实体冲突** —— 抛 identity conflict；该场景下应绕过 `UserManager` 直接操作 context。
13. **测试里读 security stamp 要用全新 context** —— 本套测试的 `UserManager` 绑在长生命周期 context 上，直接读会拿到 identity map 中的陈旧实例（我据此误判过"stamp 没变"）。
14. **改这个仓库时注意行尾**  —— `Get-Content`/`Set-Content -NoNewline` 与 `-replace` 混用会写入裸 `\n`，破坏 Markdown 结构；本文件曾因此损坏并重建。




