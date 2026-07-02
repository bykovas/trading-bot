FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/TradingBot.Worker/TradingBot.Worker.csproj src/TradingBot.Worker/
RUN dotnet restore src/TradingBot.Worker/TradingBot.Worker.csproj

COPY src/TradingBot.Worker/ src/TradingBot.Worker/
RUN dotnet publish src/TradingBot.Worker/TradingBot.Worker.csproj \
    -c Release \
    -o /app \
    --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app

# Portfolio state + event journal are written here; mount a host volume in prod.
RUN mkdir -p /app/data
ENV DOTNET_EnableDiagnostics=0

COPY --from=build /app ./

ENTRYPOINT ["dotnet", "TradingBot.Worker.dll"]
