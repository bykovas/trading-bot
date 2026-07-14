FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/TradingBot.Web/TradingBot.Web.csproj src/TradingBot.Web/
RUN dotnet restore src/TradingBot.Web/TradingBot.Web.csproj

COPY src/TradingBot.Web/ src/TradingBot.Web/
RUN dotnet publish src/TradingBot.Web/TradingBot.Web.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine
WORKDIR /app
RUN apk add --no-cache tzdata
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_PATHBASE=/web
COPY --from=build /app/publish ./
EXPOSE 8080
ENTRYPOINT ["dotnet", "TradingBot.Web.dll"]
