FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/TradingBot.Core/TradingBot.Core.csproj src/TradingBot.Core/
COPY src/TradingBot.MarketDataWorker/TradingBot.MarketDataWorker.csproj src/TradingBot.MarketDataWorker/
RUN dotnet restore src/TradingBot.MarketDataWorker/TradingBot.MarketDataWorker.csproj

COPY src/TradingBot.Core/ src/TradingBot.Core/
COPY src/TradingBot.MarketDataWorker/ src/TradingBot.MarketDataWorker/
RUN dotnet publish src/TradingBot.MarketDataWorker/TradingBot.MarketDataWorker.csproj \
    -c Release \
    -o /app \
    --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app
ENV DOTNET_EnableDiagnostics=0
COPY --from=build /app ./
ENTRYPOINT ["dotnet", "TradingBot.MarketDataWorker.dll"]
