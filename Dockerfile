# Demo 部署用：前端建置 + 後端發佈，打包成單一容器（API 與前端同一個站台）。
# 建置內容：repo 根目錄（在根目錄執行 docker build -t dynamic-modules .）。

FROM oven/bun:1.4.0 AS frontend
# 保留 frontend 與 shared 的相對位置：前端以 ../../../../shared 引用前後端共用的 JSON
WORKDIR /work/frontend
COPY frontend/package.json frontend/bun.lock ./
RUN bun install --frozen-lockfile --ignore-scripts
COPY shared/ /work/shared/
COPY frontend/ ./
# 前端版本與後端同一組 build-arg（quasar.config.ts 的 defineEnv；GIT_COMMIT 退回 RENDER_GIT_COMMIT）
ARG APP_VERSION=""
ARG GIT_COMMIT=""
ARG BUILD_TIME=""
ARG RENDER_GIT_COMMIT=""
# source map（hidden）移出要對外提供的 dist/spa，另放 /work/sourcemaps 給維運還原呼叫框（設計 T9）
RUN export BUILD_TIME="${BUILD_TIME:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}" && bunx quasar prepare && bunx quasar build \
    && cp -r dist/spa /work/sourcemaps && find /work/sourcemaps -type f ! -name '*.map' -delete && find dist/spa -name '*.map' -delete

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend
WORKDIR /src
COPY backend/ ./
# 後端內嵌 shared/i18n 與 quasar-langs.json（csproj 的 ..\..\..\shared 在這裡解析成 /shared）；少了這行 dotnet publish 會因建置檢查失敗
COPY shared/ /shared/
# 版本資訊：GIT_COMMIT 優先，退回 Render 傳入的 RENDER_GIT_COMMIT；沒給的欄位在 /api/version 顯示 dev
ARG APP_VERSION=""
ARG GIT_COMMIT=""
ARG BUILD_TIME=""
ARG RENDER_GIT_COMMIT=""
RUN COMMIT="${GIT_COMMIT:-$RENDER_GIT_COMMIT}"; BUILT="${BUILD_TIME:-$(date -u +%Y-%m-%dT%H:%M:%SZ)}"; \
    dotnet publish src/DynamicModules.WebAPI/DynamicModules.WebAPI.csproj -c Release -o /app \
      -p:AppVersion="$APP_VERSION" -p:GitCommit="$COMMIT" -p:BuildTime="$BUILT"

FROM mcr.microsoft.com/dotnet/aspnet:8.0
# 產品內備份（PostgreSQL）要 pg_dump／pg_restore（備份設計 §5.4）：從 PGDG（apt.postgresql.org）裝 postgresql-client-16，
# 和測試容器同主版本；之後支援更新的伺服器時換新版用戶端（新版用戶端能備舊版伺服器）。金鑰用 .asc 檔加 signed-by，不另裝 gnupg。
# 路徑直接指到版本目錄，不經 Debian 的 pg_wrapper；SQLite 用不到，裝著無害（Dockerfile.demo 不裝）
ADD https://www.postgresql.org/media/keys/ACCC4CF8.asc /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc
RUN . /etc/os-release \
    && echo "deb [signed-by=/usr/share/postgresql-common/pgdg/apt.postgresql.org.asc] https://apt.postgresql.org/pub/repos/apt ${VERSION_CODENAME}-pgdg main" \
      > /etc/apt/sources.list.d/pgdg.list \
    && chmod 644 /usr/share/postgresql-common/pgdg/apt.postgresql.org.asc \
    && apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client-16 \
    && rm -rf /var/lib/apt/lists/*
ENV Backup__PgDumpPath=/usr/lib/postgresql/16/bin/pg_dump
WORKDIR /app
COPY --from=backend /app ./
COPY --from=frontend /work/frontend/dist/spa ./wwwroot
COPY --from=frontend /work/sourcemaps ./sourcemaps
# SQLite 放容器內：免費方案沒有永久磁碟，重啟後回到種子資料
ENV ConnectionStrings__Dynamic="Data Source=/app/data/poc.db" \
    ASPNETCORE_ENVIRONMENT=Production \
    PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "DynamicModules.WebAPI.dll"]
