FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080

# Build the Angular application from the repository root Docker context.
FROM node:22-alpine AS frontend-build
ARG APP_VERSION=0.0.0-local
WORKDIR /src/Frontend
COPY Frontend/package*.json ./
RUN npm ci --registry=https://registry.npmjs.org --no-audit
COPY Frontend/ ./
RUN APP_VERSION=$APP_VERSION npm run build

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
ARG APP_VERSION=0.0.0-local
WORKDIR /src
COPY Backend/Rankoon.csproj Backend/
RUN dotnet restore "Backend/Rankoon.csproj"
COPY Backend/ Backend/
WORKDIR "/src/Backend"
RUN dotnet build "./Rankoon.csproj" -c $BUILD_CONFIGURATION -o /app/build -p:Version=$APP_VERSION -p:InformationalVersion=$APP_VERSION

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
ARG APP_VERSION=0.0.0-local
RUN dotnet publish "./Rankoon.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false -p:Version=$APP_VERSION -p:InformationalVersion=$APP_VERSION

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=frontend-build /src/Frontend/dist/Rankoon_Frontend/browser ./wwwroot
ENTRYPOINT ["dotnet", "Rankoon.dll"]
