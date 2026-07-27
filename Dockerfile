FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY SubtitleCleanUp.slnx ./
COPY src/SubtitleCleanUp.Core/SubtitleCleanUp.Core.csproj src/SubtitleCleanUp.Core/
COPY src/SubtitleCleanUp.Web/SubtitleCleanUp.Web.csproj src/SubtitleCleanUp.Web/
RUN dotnet restore src/SubtitleCleanUp.Web/SubtitleCleanUp.Web.csproj

COPY src/ src/
RUN dotnet publish src/SubtitleCleanUp.Web/SubtitleCleanUp.Web.csproj \
    --configuration Release \
    --output /app/publish \
    /p:UseAppHost=false \
 && test -f /app/publish/wwwroot/_framework/blazor.web.js

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
LABEL org.opencontainers.image.source="https://github.com/trembon/SubtitleCleanUp"
LABEL org.opencontainers.image.description="Review and normalize SRT subtitle names and duplicates."

RUN mkdir -p /data && chown app:app /data
COPY --from=build --chown=app:app /app/publish .
RUN test -f /app/wwwroot/_framework/blazor.web.js
USER app

ENTRYPOINT ["dotnet", "SubtitleCleanUp.Web.dll"]
