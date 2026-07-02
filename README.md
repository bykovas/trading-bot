# trading-bot

Минимальный runnable slice сейчас находится в `src/TradingBot.Worker`.

Подробный runbook: [RUNBOOK.md](RUNBOOK.md).

Он делает один консольный цикл:

1. берет candidate universe из `appsettings.json`;
2. выбирает активный watchlist через AI-advisor или fallback heuristic;
3. считает EMA/RSI по выбранным инструментам;
4. выдает детерминированное решение `NONE` / `LONG_MICRO`;
5. прогоняет risk gate;
6. не отправляет ордера.

ИИ пока используется только для выбора, за какими парами следить. Он не участвует в trade decision и не может отдать приказ купить или продать.

## Запуск без секретов

По умолчанию используется sample market data:

```bash
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

## Запуск с публичными данными Kraken

Для OHLC/AssetPairs/Ticker API-ключ не нужен:

```bash
TRADINGBOT_MARKET_DATA_MODE=kraken \
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

## Ночной dry-run

```bash
TRADINGBOT_MARKET_DATA_MODE=kraken \
TRADINGBOT_RUN_ONCE=false \
TRADINGBOT_LOOP_INTERVAL_SECONDS=300 \
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

Результаты пишутся в:

```text
data/dry-run/portfolio-state.json
data/dry-run/events.jsonl
```

Стартовый виртуальный портфель задается в `src/TradingBot.Worker/appsettings.json` в блоке `Portfolio`.

## Включить AI watchlist advisor

Worker поддерживает OpenAI-compatible chat completions endpoint. Ключи не хранятся в репозитории:

```bash
TRADINGBOT_AI_PROVIDER=openai-compatible \
TRADINGBOT_AI_BASE_URL=https://api.openai.com/v1 \
TRADINGBOT_AI_MODEL=your-model \
TRADINGBOT_AI_API_KEY=your-key \
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

Если AI недоступен, ответ невалидный или модель вернула пары вне конфигурации, worker безопасно падает обратно на heuristic watchlist.

## Важные ограничения

- `LiveTradingEnabled=false` по умолчанию.
- Даже при `LiveTradingEnabled=true` этот slice не отправляет ордера: live execution явно заблокирован до отдельной реализации Kraken private API.
- Candidate universe задается человеком в `appsettings.json`; AI выбирает только из этого списка и не сканирует весь Kraken listing.
