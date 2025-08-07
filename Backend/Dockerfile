FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080

# Build the Angular application from the repository root Docker context.
FROM node:22-alpine AS frontend-build
WORKDIR /src/Frontend
COPY Frontend/package*.json ./
RUN npm ci
COPY Frontend/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY Backend/Rankoon.csproj Backend/
RUN dotnet restore "Backend/Rankoon.csproj"
COPY Backend/ Backend/
WORKDIR "/src/Backend"
RUN dotnet build "./Rankoon.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Rankoon.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=frontend-build /src/Frontend/dist/Rankoon_Frontend/browser ./wwwroot
ENTRYPOINT ["dotnet", "Rankoon.dll"]
