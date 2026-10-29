# Openhold server image. Build and run it with compose.yaml (see docs/install/docker.md).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/Openhold.Core/Openhold.Core.csproj src/Openhold.Core/
COPY src/Openhold.Server/Openhold.Server.csproj src/Openhold.Server/
RUN dotnet restore src/Openhold.Server/Openhold.Server.csproj
COPY src/Openhold.Core/ src/Openhold.Core/
COPY src/Openhold.Server/ src/Openhold.Server/
RUN dotnet publish src/Openhold.Server/Openhold.Server.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# The sign-in library loads the system's Kerberos library at start, even when Windows sign-in is off.
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
# Everything the server keeps (file store, token signing key, logs) lives under /data, owned by the unprivileged
# account the server runs as.
RUN mkdir -p /data && chown "$APP_UID" /data
VOLUME /data
USER $APP_UID
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Openhold__AuthMode=Production \
    Openhold__Authentication__Windows=false \
    Openhold__Authentication__SigningKeyPath=/data/keys/token-signing-key.pem \
    Openhold__LogFolder=/data/logs
# Openhold protects nothing with ASP.NET data protection, so its warning about unpersisted keys is noise.
ENV Logging__LogLevel__Microsoft.AspNetCore.DataProtection=Error
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s CMD ["dotnet", "Openhold.Server.dll", "--health-check"]
ENTRYPOINT ["dotnet", "Openhold.Server.dll"]
