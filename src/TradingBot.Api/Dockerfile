FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/TradingBot.Core/TradingBot.Core.csproj src/TradingBot.Core/
COPY src/TradingBot.Api/TradingBot.Api.csproj src/TradingBot.Api/
RUN dotnet restore src/TradingBot.Api/TradingBot.Api.csproj

COPY src/TradingBot.Core/ src/TradingBot.Core/
COPY src/TradingBot.Api/ src/TradingBot.Api/
RUN dotnet publish src/TradingBot.Api/TradingBot.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine
WORKDIR /app

RUN apk add --no-cache tzdata

ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /app/publish ./

EXPOSE 8080
ENTRYPOINT ["dotnet", "TradingBot.Api.dll"]
