# Accountix .NET host for Azure App Service (Linux) or any container host.
# Build from the REPOSITORY ROOT. /static (CSS/JS) is part of the publish output
# (src/Accountix.Web.Shared/static, copied by the build):
#
#   docker build -f dotnet/deploy/Dockerfile -t accountix-web .
#   docker run -p 8080:8080 -e SECRET_KEY=... -e DATABASE_URL=postgresql://... accountix-web
#
# docs/DEPLOY.md lists every setting. Nothing here contacts Azure or Neon.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Project files first, so a code change does not re-download every package.
COPY dotnet/Accountix.slnx dotnet/
COPY dotnet/src/Accountix.Core/*.csproj dotnet/src/Accountix.Core/
COPY dotnet/src/Accountix.Web.Shared/*.csproj dotnet/src/Accountix.Web.Shared/
COPY dotnet/src/Accountix.Web/*.csproj dotnet/src/Accountix.Web/
COPY dotnet/src/Accountix.Admin/*.csproj dotnet/src/Accountix.Admin/
COPY dotnet/src/Accountix.Migrations/*.csproj dotnet/src/Accountix.Migrations/
COPY dotnet/src/Modules/ dotnet/src/Modules/
RUN find dotnet/src/Modules -type f ! -name '*.csproj' -delete \
 && dotnet restore dotnet/src/Accountix.Web/Accountix.Web.csproj -r linux-x64 -p:PublishReadyToRun=true
COPY dotnet/src/ dotnet/src/
# ReadyToRun: compiled ahead of time, so a new instance (scale-out, slot swap,
# restart) answers its first requests without waiting for the JIT.
RUN dotnet publish dotnet/src/Accountix.Web/Accountix.Web.csproj -c Release -r linux-x64 --self-contained false \
      --no-restore -p:PublishReadyToRun=true -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
# The aspnet image's unprivileged user. Health: App Service probes /net/health
# (docs/DEPLOY.md); the image has no curl, so there is no HEALTHCHECK here.
USER $APP_UID
ENTRYPOINT ["dotnet", "Accountix.Web.dll"]
