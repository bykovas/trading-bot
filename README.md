# trading-bot

Минимальный runnable slice сейчас находится в `src/TradingBot.SpotWorker`.

Futures scaffold находится в `src/TradingBot.FuturesWorker`. Он запускает
отдельный dry-run-only цикл с virtual margin ledger, TP/SL simulation и
read-only public Kraken Futures market data. Live futures execution path в
бинарнике отсутствует.

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
dotnet run --project src/TradingBot.SpotWorker/TradingBot.SpotWorker.csproj
```

## Запуск с публичными данными Kraken

Для OHLC/AssetPairs/Ticker API-ключ не нужен:

```bash
TRADINGBOT_MARKET_DATA_MODE=kraken \
dotnet run --project src/TradingBot.SpotWorker/TradingBot.SpotWorker.csproj
```

## Ночной dry-run

```bash
TRADINGBOT_MARKET_DATA_MODE=kraken \
TRADINGBOT_RUN_ONCE=false \
TRADINGBOT_LOOP_INTERVAL_SECONDS=300 \
dotnet run --project src/TradingBot.SpotWorker/TradingBot.SpotWorker.csproj
```

Результаты пишутся в:

```text
data/dry-run/portfolio-state.json
data/dry-run/events.jsonl
```

## Futures dry-run

Sample futures loop:

```bash
TRADINGBOT_MARKET_DATA_MODE=sample \
TRADINGBOT_RUN_ONCE=true \
dotnet run --project src/TradingBot.FuturesWorker/TradingBot.FuturesWorker.csproj
```

Public Kraken Futures data (default for the futures worker and deploy envs):

```bash
TRADINGBOT_MARKET_DATA_MODE=kraken-futures \
TRADINGBOT_RUN_ONCE=true \
dotnet run --project src/TradingBot.FuturesWorker/TradingBot.FuturesWorker.csproj
```

Futures worker reads `/derivatives/api/v3/instruments`,
`/derivatives/api/v3/tickers`, and mark candles from `/api/charts/v1/mark/...`.
It still only writes virtual fills; there is no futures live-trading override.

Стартовый виртуальный портфель задается в `src/TradingBot.SpotWorker/appsettings.json` в блоке `Portfolio`.

## Execution policy и position exit

Блоки `ExecutionPolicy` и `PositionExit` в `appsettings.json` делают dry-run ближе к будущему live-поведению: они не дают боту churn'ить позицию на шуме и добавляют детерминированные правила выхода. Реальные ордера не отправляются.

```json
"ExecutionPolicy": {
  "CooldownAfterBuySeconds": 900,
  "CooldownAfterSellSeconds": 1800,
  "MinHoldSeconds": 900,
  "AllowImmediateExitOnSignalFlip": false
},
"PositionExit": {
  "MinProfitToExitOnSignalFlipPercent": 1.2,
  "StopLossPercent": 1.5,
  "TakeProfitPercent": 2.0,
  "MaxHoldMinutes": 240
}
```

- `MinHoldSeconds` не дает купить и почти сразу продать ту же позицию из-за шумного EMA-флипа. Если позиция моложе `MinHoldSeconds`, обычный signal flip (`current=LONG`, `desired=NONE`) выводит `WOULD_HOLD`, а не `WOULD_SELL`.
- `MinProfitToExitOnSignalFlipPercent` разрешает выход по обычному signal flip только если консервативный unrealized PnL не ниже порога.
- `StopLossPercent`, `TakeProfitPercent`, `MaxHoldMinutes` - это hard exits. Они закрывают позицию даже если min-hold/min-profit иначе заблокировали бы продажу (а take-profit срабатывает даже когда стратегия все еще хочет `LONG_MICRO`).
- Обычные signal flips НЕ должны мгновенно churn'ить позиции; hard exits (kill switch, stop-loss, take-profit, max-hold) ОБХОДЯТ min-hold и min-profit.
- `MinHoldSeconds` НЕ значит "держать вечно" — это только защита от шума.

Приоритет выходов, совместимость со старым state и коды причин (`SELL_STOP_LOSS`, `MIN_HOLD_BLOCK` и т.д.) описаны в [RUNBOOK.md](RUNBOOK.md).

## Включить AI watchlist advisor

Worker поддерживает OpenAI-compatible chat completions endpoint. Ключи не хранятся в репозитории:

```bash
TRADINGBOT_AI_PROVIDER=openai-compatible \
TRADINGBOT_AI_BASE_URL=https://api.openai.com/v1 \
TRADINGBOT_AI_MODEL=your-model \
TRADINGBOT_AI_API_KEY=your-key \
dotnet run --project src/TradingBot.SpotWorker/TradingBot.SpotWorker.csproj
```

Если AI недоступен, ответ невалидный или модель вернула пары вне конфигурации, worker безопасно падает обратно на heuristic watchlist.

## Важные ограничения

- `LiveTradingEnabled=false` по умолчанию.
- Даже при `LiveTradingEnabled=true` этот slice не отправляет ордера: live execution явно заблокирован до отдельной реализации Kraken private API.
- Candidate universe задается человеком в `appsettings.json`; AI выбирает только из этого списка и не сканирует весь Kraken listing.
