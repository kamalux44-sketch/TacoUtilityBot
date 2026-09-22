FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["TacoUtilityBot/TacoUtilityBot.csproj", "TacoUtilityBot/"]
RUN dotnet restore "./TacoUtilityBot/TacoUtilityBot.csproj"
COPY . .
WORKDIR "/src/TacoUtilityBot"
RUN dotnet build "./TacoUtilityBot.csproj" -c "$BUILD_CONFIGURATION" --no-restore -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./TacoUtilityBot.csproj" -c "$BUILD_CONFIGURATION" --no-restore -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "TacoUtilityBot.dll"]