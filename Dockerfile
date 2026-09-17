# PdnodeVote — 多阶段构建
#
# 构建阶段用 SDK 镜像编译（含 Blazor WebAssembly 目标包），运行阶段只带 ASP.NET Core 运行时。
# 注意：这是一个 Core Hosted 方案 —— 服务端项目引用 PdnodeVote.Client（WASM），
# 所以两个 csproj 都必须进入构建上下文。

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先只拷贝项目文件，让 restore 层能被 Docker 缓存
COPY PdnodeVote.slnx ./
COPY PdnodeVote.csproj ./
COPY PdnodeVote.Client/PdnodeVote.Client.csproj PdnodeVote.Client/

RUN dotnet restore PdnodeVote.csproj

# 再拷贝源码并发布
COPY . .
RUN dotnet publish PdnodeVote.csproj \
        -c Release \
        -o /app/publish \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# SQLite 需要 ICU；不装额外包，运行时镜像自带。
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true

COPY --from=build /app/publish .

# 运行时可写入目录：
#   /app/Data            → SQLite 数据库（连接串 "Data/app.db" 相对于 ContentRootPath，即 /app）
#   /app/wwwroot/uploads → 用户上传的图片（UseStaticFiles 从这里读取）
# 两者都必须挂卷，否则重建容器会丢失数据。
RUN mkdir -p /app/Data /app/wwwroot/uploads \
    && chown -R $APP_UID:$APP_UID /app/Data /app/wwwroot/uploads

VOLUME ["/app/Data", "/app/wwwroot/uploads"]

USER $APP_UID
EXPOSE 8080

# 健康检查：aspnet 运行时镜像里没有 curl/wget/nc（已实测），但自带 bash，
# 所以用 bash 的内置 /dev/tcp 发起一次 TCP 连接并读取 HTTP 状态行。
# 收到 2xx/3xx 即视为健康。
HEALTHCHECK --interval=30s --timeout=5s --start-period=25s --retries=3 \
    CMD bash -c 'exec 3<>/dev/tcp/127.0.0.1/8080 && printf "GET / HTTP/1.0\r\nHost: localhost\r\n\r\n" >&3 && head -1 <&3 | grep -qE " 2[0-9][0-9] | 3[0-9][0-9] "'

ENTRYPOINT ["dotnet", "PdnodeVote.dll"]
