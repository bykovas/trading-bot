# Trading Bot Runbook

Этот файл объясняет, как локально запускать текущий минимальный worker и как читать его консольный вывод.

Текущее состояние: decision loop с виртуальным (dry-run) портфелем. Worker берёт sample-данные или публичные данные Kraken, выбирает активный watchlist, считает EMA/RSI, выдаёт `NONE` или `LONG_MICRO`, прогоняет risk gate, симулирует сделки в виртуальном портфеле и пишет результат в консоль/файлы.

Дополнительно: если заданы приватные ключи Kraken, worker умеет отправлять ордер на биржу в режиме проверки `validate=true` (без исполнения) и, при явно включённом `LiveTradingEnabled=true`, реальные микро-ордера. См. раздел «Реальные ордера на Kraken (validate → live)» ниже и [docs/implementation/00-live-kraken-ordering.md](docs/implementation/00-live-kraken-ordering.md).

ИИ сейчас используется только для выбора, за чем следить. В торговом решении он не участвует.

## Где лежит конфиг (единый источник)

Весь конфиг worker'а — в одном файле `src/TradingBot.Worker/appsettings.json`: режим данных, интервал, риск, стратегия, `Trading.LiveTradingEnabled` и оба API-ключа (`Kraken.ApiKey`/`Kraken.ApiSecret` и `Ai.ApiKey`).

- Локально можно временно перебить любое значение через переменную окружения `TRADINGBOT_*` (см. `.env.example`), но это опционально.
- На сервере правится один файл `/opt/trading-bot/appsettings.json` (смонтирован с хоста, в git не попадает — туда можно вписывать реальные ключи), затем `docker restart trading-bot-worker`.
- В репозитории ключи в `appsettings.json` всегда пустые. Реальные значения не коммитить.

## Быстрый запуск

Из корня репозитория:

```bash
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

Это запускает sample market data. Сеть и ключи не нужны.

## Запуск на публичных данных Kraken

```bash
TRADINGBOT_MARKET_DATA_MODE=kraken \
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

API-ключ Kraken для этого не нужен. Используются только публичные endpoints `AssetPairs`, `OHLC` и `Ticker`.

## Запуск с AI watchlist advisor

```bash
TRADINGBOT_MARKET_DATA_MODE=kraken \
TRADINGBOT_AI_PROVIDER=openai-compatible \
TRADINGBOT_AI_BASE_URL=https://api.openai.com/v1 \
TRADINGBOT_AI_MODEL=your-model \
TRADINGBOT_AI_API_KEY=your-key \
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

Ключи можно задать в `appsettings.json` (`Ai.ApiKey`) — на сервере это файл `/opt/trading-bot/appsettings.json`, он не в git. В репозитории поле держим пустым и реальные значения не коммитим. Локально можно перебить через env, как выше.

Если AI недоступен, не задан ключ, не задана модель или AI вернул неподходящие пары, worker использует fallback `heuristic` и продолжает работу.

## Где менять пары

Файл:

```text
src/TradingBot.Worker/appsettings.json
```

Блок:

```json
"CandidateUniverse": [
  {
    "Pair": "BTC/EUR",
    "KrakenPair": "XXBTZEUR",
    "Venue": "Kraken",
    "Enabled": true
  }
]
```

`CandidateUniverse` - это список пар, которые вообще разрешено рассматривать. AI не может добавить пару сам. Он может только выбрать активный watchlist из этого списка.

Текущий стартовый universe:

```text
UNI/EUR -> UNIEUR
BTC/EUR -> XXBTZEUR
XLM/EUR -> XXLMZEUR
SOL/EUR -> SOLEUR
DOT/EUR -> DOTEUR
```

## Где задать стартовый dry-run портфель

Файл:

```text
src/TradingBot.Worker/appsettings.json
```

Блок:

```json
"Portfolio": {
  "StartingCashEur": 50,
  "Positions": []
}
```

Значения:

- `StartingCashEur` - стартовый виртуальный cash.
- `Pair` - пара, которая уже есть на руках.
- `Side` - сейчас поддерживается только `LONG`.
- `Quantity` - количество base asset, например ETH.
- `EntryPrice` - цена входа.
- `EntryNotionalEur` - сколько EUR было вложено при входе.

Важно: если файл `data/dry-run/portfolio-state.json` уже существует и валиден, worker продолжит состояние оттуда, а не из `appsettings.json`. Это нужно для ночного dry-run, чтобы виртуальные покупки/продажи не сбрасывались каждый цикл.

Если файла нет, он пустой или битый (невалидный JSON), worker не падает: он создает новый портфель из `appsettings.json` (по умолчанию `StartingCashEur = 50`, без позиций) и сразу сохраняет его. В консоли это видно по строке `portfolio-load:`:

```text
portfolio-load: no state file at data/dry-run/portfolio-state.json; creating a fresh portfolio with 50 EUR
portfolio-load: reusing existing state from data/dry-run/portfolio-state.json (cash 47 EUR, positions 1)
portfolio-load: existing state ... is empty or invalid; creating a fresh portfolio with 50 EUR
```

Чтобы начать dry-run заново:

```bash
rm -rf data/dry-run
```

## Как читать стартовый блок

Пример:

```text
Blynai Capital worker
marketDataMode=kraken
aiProvider=none
liveTradingEnabled=False
timeframe=5m maxActive=5
aiInTradeDecision=false
```

Значения:

- `marketDataMode=sample` - используются сгенерированные тестовые свечи.
- `marketDataMode=kraken` - используются реальные публичные свечи Kraken.
- `aiProvider=none` - AI не включен даже для watchlist.
- `aiProvider=openai-compatible` - worker попробует использовать AI для выбора watchlist.
- `liveTradingEnabled=False` - live trading выключен.
- `timeframe=5m` - свечи с интервалом 5 минут.
- `maxActive=5` - worker выберет максимум 5 активных пар из `CandidateUniverse`.
- `aiInTradeDecision=false` - AI не влияет на `desired`, `score`, risk и execution.

Важно: в текущем skeleton реальные ордера не отправляются даже если поставить `LiveTradingEnabled=true`.

## Как читать candidate universe

Пример:

```text
candidate-universe:
  SOL/EUR price=71.19 bid=71.18 ask=71.20 change=-1.18% vol=0.47% status=online data=ok
  BTC/EUR price=54109.3 bid=54108.1 ask=54110.2 change=0.87% vol=0.25% status=online data=ok
```

Значения:

- `SOL/EUR` - торговая пара.
- `price=71.19` - последняя закрытая цена из свечей.
- `bid=71.18` - лучшая цена, по которой dry-run считает продажу до slippage/fee.
- `ask=71.20` - лучшая цена, по которой dry-run считает покупку до slippage/fee.
- `change=-1.18%` - изменение за последние доступные свечи короткого окна.
- `vol=0.47%` - средний диапазон свечей относительно цены; грубая оценка краткосрочной волатильности.
- `status=online` - статус пары из Kraken `AssetPairs`.
- `data=ok` - данные пригодны для расчета.

Если вместо `data=ok` написана ошибка, например DNS/network error, то по этой паре решение не считается.

## Как читать watchlist advisor

Пример без AI:

```text
watchlist-advisor provider=heuristic:
  #1 SOL/EUR: heuristic pick: volume 628.9924, volatility 0.47%
  #2 UNI/EUR: heuristic pick: volume 282.1893, volatility 0.41%
```

Значения:

- `provider=heuristic` - AI не использовался; пары выбраны простым правилом.
- `#1`, `#2` - приоритет в активном watchlist.
- `reason` после двоеточия - почему пара выбрана.

Пример с AI:

```text
watchlist-advisor provider=openai-compatible:
  #1 SOL/EUR: selected because liquidity is usable and volatility is controlled
```

Даже когда provider AI, он только выбирает, за чем следить. Он не говорит покупать или продавать.

Открытые позиции всегда добавляются в evaluation принудительно, даже если AI/watchlist advisor их не выбрал. В консоли это видно так:

```text
watchlist-forced UNI/EUR: open position must be evaluated even if advisor did not select it
```

Это важная защита: бот не должен игнорировать уже открытые позиции только потому, что advisor выбрал другие пары.

## Как читать decision block

Пример:

```text
decision UNI/EUR:
  price=3197.7665 ema9=3201.4277 ema21=3202.1063 rsi14=31.92
  desired=NONE score=0.15 targetEur=0
  signal EMA: -0.25 fast EMA is below slow EMA
  signal RSI: 0 RSI 31.92 is neutral
  signal Volatility: +0.05 short-term volatility 0.23% is controlled
  risk=APPROVED
  risk-reason: no position requested
  execution=NO_ORDER
```

Построчно:

- `decision UNI/EUR:` - worker считает решение для пары `UNI/EUR`.
- `price=3197.7665` - последняя закрытая цена свечи.
- `ema9=3201.4277` - быстрая EMA за 9 свечей.
- `ema21=3202.1063` - медленная EMA за 21 свечу.
- `rsi14=31.92` - RSI за 14 свечей.
- `desired=NONE` - желаемая позиция: не открывать позицию.
- `desired=LONG_MICRO` - если появится такое значение, это значит "хотел бы открыть микропозицию".
- `score=0.15` - итоговая оценка сигнала от `0` до `1`.
- `targetEur=0` - целевой размер позиции в EUR. При `NONE` всегда `0`.
- `signal EMA: -0.25` - вклад EMA в score. Минус значит против входа.
- `fast EMA is below slow EMA` - быстрая EMA ниже медленной, краткосрочный тренд слабее долгосрочного.
- `signal RSI: 0` - вклад RSI нейтральный.
- `RSI 31.92 is neutral` - RSI низкий, но не настолько, чтобы стратегия дала дополнительный плюс.
- `signal Volatility: +0.05` - волатильность дала небольшой плюс.
- `short-term volatility 0.23% is controlled` - краткосрочная волатильность не выглядит чрезмерной.
- `risk=APPROVED` - Risk Manager не нашел причины заблокировать proposal.
- `risk-reason: no position requested` - риска открытия нет, потому что `desired=NONE`.
- `execution=NO_ORDER` - ордер не нужен и не отправляется.

Теперь decision block также может содержать портфельные строки:

```text
position=LONG qty=0.002 entry=3100 value=6.39 pnl=+3.15%
execution=WOULD_SELL
execution-reason: close virtual long, realized PnL EUR 0.1953 (+3.15%)
portfolio-cash: 18.80 -> 25.19 EUR
portfolio-value: 25.19 -> 25.19 EUR
```

Это значит:

- `position=LONG ...` - перед решением у нас уже была виртуальная позиция.
- `execution=WOULD_SELL` - dry-run закрыл бы позицию.
- `execution-reason` - почему и с каким P&L.
- `portfolio-cash` - cash до и после виртуального действия.
- `portfolio-value` - total value до и после виртуального действия.

При `WOULD_BUY`/`WOULD_SELL` также печатается:

```text
fill-price=2.7629 fee=0.0078 gross=2.9922 net=3
```

Значения:

- `fill-price` - расчетная цена исполнения.
- `fee` - комиссия в EUR.
- `gross` - notional до комиссии.
- `net` - cash effect после комиссии.

## Что означает score

`score` - это простая детерминированная сумма вкладов.

Сейчас стартовая база:

```text
base score = 0.35
```

Потом добавляются или вычитаются факторы:

- EMA bullish: `+0.30`
- EMA bearish: `-0.25`
- RSI acceptable: `+0.15`
- RSI oversold: `+0.08`
- RSI overheated: `-0.25`
- volatility controlled: `+0.05`
- volatility elevated: `-0.10`

Если итоговый score больше или равен `Strategy.MinimumLongScore`, сейчас по умолчанию `0.55`, worker выставляет:

```text
desired=LONG_MICRO
```

Иначе:

```text
desired=NONE
```

## Что означает EMA

EMA - exponential moving average, экспоненциальная скользящая средняя.

- `ema9` быстрее реагирует на цену.
- `ema21` медленнее и показывает более длинный контекст.

Простая логика:

- `ema9 > ema21` - краткосрочный тренд сильнее, это плюс к long-сценарию.
- `ema9 < ema21` - краткосрочный тренд слабее, это минус.

## Что означает RSI

RSI - momentum indicator от `0` до `100`.

В текущей простой логике:

- `35-68` - acceptable range, небольшой плюс.
- `<30` - oversold, маленький плюс.
- `>75` - overheated, минус.
- остальное - neutral.

Это не "истина рынка", а первое простое правило для runnable skeleton.

## Что означает risk

Risk Manager смотрит на proposal после decision engine.

Возможные значения:

- `risk=APPROVED` - proposal прошел risk gate.
- `risk=REJECTED` - proposal заблокирован.

Примеры причин reject:

- `kill switch is active`
- `target EUR exceeds max order EUR`
- `target notional is zero`

При `desired=NONE` risk обычно будет `APPROVED`, потому что бот ничего не открывает.

## Что означает execution

Возможные значения сейчас:

- `NO_ORDER` - нет нужного действия.
- `WOULD_BUY` - dry-run открыл бы виртуальную long-позицию.
- `WOULD_SELL` - dry-run закрыл бы виртуальную long-позицию.
- `WOULD_HOLD` - позиция удерживается. Разные случаи различаются по строке `execution-hold-reason-code`:
  - `DESIRED_LONG` - позиция уже есть и желаемое состояние тоже `LONG_MICRO`.
  - `MIN_HOLD_BLOCK` - стратегия дала signal flip (`desired=NONE`), но позиция моложе `ExecutionPolicy.MinHoldSeconds`, поэтому обычная продажа по флипу подавлена.
  - `MIN_PROFIT_BLOCK` - signal flip, позиция уже старше `MinHoldSeconds`, но консервативный unrealized PnL ниже `PositionExit.MinProfitToExitOnSignalFlipPercent`.
- `WOULD_SELL` - позиция закрывается. Причина различается по строке `execution-exit-reason-code`:
  - `SELL_STOP_LOSS` - сработал stop-loss.
  - `SELL_TAKE_PROFIT` - сработал take-profit.
  - `SELL_MAX_HOLD` - достигнут max-hold по времени.
  - `SELL_KILL_SWITCH` - активен kill switch, позиция принудительно закрывается.
  - `SELL_SIGNAL_FLIP` - обычный signal flip прошел все soft-guard'ы.
- `WOULD_BUY_BLOCKED` - бот хотел бы купить, но dry-run/risk constraints не позволяют, например не хватает cash, достигнут `MaxOpenPositions`, или активен pair cooldown (`execution-hold-reason-code=COOLDOWN_BLOCK`).
- `REJECTED` - Risk Manager заблокировал proposal.

Это сделано намеренно: текущий worker должен показывать решения в консоли, а не торговать.

## Execution policy и position exit

Dry-run использует эти правила, чтобы симулировать будущее live-поведение реалистичнее и не терять деньги на fee/spread/slippage из-за мгновенного churn. Реальные ордера при этом не отправляются.

Два блока в `appsettings.json`:

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

### ExecutionPolicy

- `MinHoldSeconds` не дает боту купить и почти сразу продать ту же позицию из-за шумного EMA-флипа. Пример проблемы: в 16:28 купили SOL/EUR (`WOULD_BUY`), в 16:30 продали (`WOULD_SELL`), realized PnL ушел в минус в основном из-за fee/spread/slippage при крошечном изменении сигнала.
- Если позиция открыта меньше `MinHoldSeconds` назад и `AllowImmediateExitOnSignalFlip=false`, обычный signal flip (`current=LONG`, `desired=NONE`) НЕ закрывает позицию. Вместо `WOULD_SELL` выводится `WOULD_HOLD` (`MIN_HOLD_BLOCK`) с reason `minimum hold active: signal flip ignored until position age reaches {MinHoldSeconds}s`. Состояние портфеля не меняется.
- `CooldownAfterBuySeconds` / `CooldownAfterSellSeconds` не дают снова купить ту же пару слишком быстро после покупки/продажи. При активном cooldown покупка выводится как `WOULD_BUY_BLOCKED` (`COOLDOWN_BLOCK`).

### PositionExit

- `MinProfitToExitOnSignalFlipPercent` - обычный signal flip закрывает позицию только если консервативный unrealized PnL не ниже этого порога. Если PnL ниже, выводится `WOULD_HOLD` (`MIN_PROFIT_BLOCK`). Это правило применяется ТОЛЬКО к обычному signal flip и не блокирует hard exits.
- `StopLossPercent` - если консервативный unrealized PnL <= `-StopLossPercent`, позиция закрывается (`SELL_STOP_LOSS`) даже если `MinHoldSeconds` еще не прошел.
- `TakeProfitPercent` - если PnL >= `TakeProfitPercent`, позиция закрывается (`SELL_TAKE_PROFIT`) даже если стратегия все еще хочет `LONG_MICRO`.
- `MaxHoldMinutes` - если возраст позиции >= `MaxHoldMinutes`, позиция закрывается (`SELL_MAX_HOLD`), даже если min-hold/min-profit иначе заблокировали бы продажу. `0` отключает это правило.

### Приоритет выходов (детерминированный)

Для открытой позиции правила проверяются строго в этом порядке:

1. Kill switch / emergency exit (`SELL_KILL_SWITCH`) - использует существующий `Risk.KillSwitch`.
2. Stop-loss (`SELL_STOP_LOSS`).
3. Take-profit (`SELL_TAKE_PROFIT`).
4. Max-hold (`SELL_MAX_HOLD`).
5. Обычная сверка с desired-позицией:
   - `current=NONE` + `desired=LONG_MICRO` -> BUY;
   - `current=LONG` + `desired=LONG_MICRO` -> HOLD (`DESIRED_LONG`);
   - `current=LONG` + `desired=NONE` -> возможно SELL, но только после `MinHoldSeconds` и `MinProfitToExitOnSignalFlipPercent`;
   - `current=NONE` + `desired=NONE` -> NO_ORDER.

Важно:

- Пункты 1-4 - это hard exits. Они ОБХОДЯТ `MinHoldSeconds` и `MinProfitToExitOnSignalFlipPercent`.
- `MinHoldSeconds` НЕ означает "держать вечно". Это только защита от мгновенного churn на шумных флипах; hard exits всегда могут закрыть позицию.
- Логика выхода вынесена в чистую функцию `PositionExitPolicy.EvaluateHeldPosition` и покрыта unit-тестами (`tests/TradingBot.Worker.Tests`).

Совместимость со старым state: старые `portfolio-state.json` без полей `openedAtUtc` / `lastActionAtUtc` загружаются без падения. Если у существующей позиции нет `openedAtUtc`, она считается "достаточно старой", и min-hold/max-hold ее не трогают неожиданно (наименее сюрпризное поведение: старые позиции закрываются как раньше, без форсированной продажи сразу после апгрейда формата). Новые позиции всегда получают `openedAtUtc` в момент открытия.

## Как dry-run считает цену сделки

Dry-run не использует последнюю цену свечи как идеальную цену исполнения. Это было бы слишком оптимистично.

Для Kraken public mode worker берет `Ticker`:

- покупка считается от `ask`;
- продажа считается от `bid`;
- сверху добавляется `SlippageBps`;
- комиссия считается через `TakerFeeBps`.

Настройки:

```json
"DryRun": {
  "TakerFeeBps": 26,
  "SlippageBps": 5
}
```

`26 bps` = `0.26%`, стартовая taker fee оценка для Kraken Pro low-volume tier.

`5 bps` = `0.05%`, дополнительный буфер на проскальзывание.

Покупка:

```text
fillPrice = ask * (1 + slippageBps / 10000)
grossNotional = targetOrderEur / (1 + feeRate)
fee = targetOrderEur - grossNotional
quantity = grossNotional / fillPrice
cash -= targetOrderEur
```

Продажа:

```text
fillPrice = bid * (1 - slippageBps / 10000)
grossExit = quantity * fillPrice
fee = grossExit * feeRate
cash += grossExit - fee
```

Позиции mark-to-market тоже считаются консервативно: как будто позицию пришлось бы продать сейчас по `bid - slippage - fee`. Поэтому после `WOULD_BUY` total portfolio value обычно сразу немного ниже стартового cash. Это нормально: так виден spread + комиссия.

## Как сейчас с продажами

В dry-run продажи теперь считаются через desired-position модель.

Worker считает желаемое состояние:

```text
desired=NONE
desired=LONG_MICRO
```

Потом сравнивает его с виртуальным портфелем:

```text
desired=NONE, позиции нет        -> ничего не делать
desired=NONE, позиция уже есть   -> надо закрывать / продавать
```

Логика:

```text
current=NONE       desired=LONG_MICRO -> BUY / open
current=LONG_MICRO desired=LONG_MICRO -> HOLD
current=LONG_MICRO desired=NONE       -> SELL / close
current=NONE       desired=NONE       -> NO_ORDER
```

То есть Decision Engine не говорит "buy" или "sell" напрямую. Он говорит, какая позиция должна быть. Dry-run execution сравнивает желаемое состояние с тем, что уже есть на руках, и только после этого выводит `WOULD_BUY`, `WOULD_SELL`, `WOULD_HOLD` или `NO_ORDER`.

## Учитывает ли worker то, что уже куплено

Да, в dry-run режиме учитывает виртуальный портфель.

Источник портфеля:

1. первый запуск: `Portfolio` из `appsettings.json`;
2. последующие запуски: `data/dry-run/portfolio-state.json`.

Console output может выглядеть так:

```text
position current=LONG qty=0.002 entry=3100 notional=6.20 pnl=+1.95%
desired=NONE score=0.15 targetEur=0
execution=WOULD_SELL reason=desired none while current long
```

Правильная интеграция с реальным Kraken будет позже через private API: `Balance`, `TradesHistory`, потом реконструкция позиции. Сейчас это intentionally dry-run.

## Сработает ли продажа, если AI нашел плохую новость

Сейчас нет.

Причины:

- текущий AI используется только для выбора watchlist;
- AI не участвует в `desired`;
- AI не получает новостей из внешнего поиска;
- AI не получает текущие позиции;
- продаж в execution layer еще нет.

Правильная будущая логика должна быть осторожной:

```text
если позиции нет и AI risk HIGH -> запретить новый BUY
если позиция есть и AI risk HIGH -> предложить RISK_EXIT
если AI недоступен/старый/невалидный -> не использовать AI
```

Но я бы не делал "AI увидел что-то страшное -> мгновенно продать" без правил. Лучше так:

```text
AI eventRisk=HIGH
position current=LONG
technical state=OK
risk policy=AI_HIGH_RISK_EXIT
desired=NONE
execution=WOULD_SELL
```

То есть AI не отправляет sell сам. Он создает структурированный risk flag. Детерминированное правило решает, что при открытой позиции и `eventRisk=HIGH` желаемая позиция становится `NONE`. Потом execution сравнивает `current=LONG` и `desired=NONE` и получает продажу.

Так это остается объяснимым и воспроизводимым.

## Что сейчас отправляется в AI

Сейчас AI получает только summary по кандидатам из `CandidateUniverse`.

Отправляется примерно такая структура:

```json
{
  "task": "Select up to 5 pairs to watch for the next deterministic decision cycle.",
  "requiredSchema": {
    "recommended": [
      {
        "pair": "SOL/EUR",
        "priority": 1,
        "reason": "short reason without trade instruction"
      }
    ],
    "warnings": ["optional warning"]
  },
  "candidates": [
    {
      "pair": "SOL/EUR",
      "venue": "Kraken",
      "usable": true,
      "warning": null,
      "lastPrice": 71.19,
      "changePercent": -1.18,
      "volatilityPercent": 0.47,
      "lastVolume": 628.9924,
      "pairStatus": "online"
    }
  ]
}
```

AI system instruction сейчас жестко говорит:

```text
You are a watchlist selection assistant for a deterministic trading bot.
You do not make trade decisions. You do not say buy or sell.
Select only from the configured candidate pairs. Never invent symbols.
Prefer liquid, usable candidates with controlled volatility and clear market data.
Return strict JSON only.
```

То есть AI сейчас не получает:

- твой баланс;
- что у тебя уже куплено;
- цену входа;
- P&L;
- API keys;
- private Kraken data;
- весь Kraken listing;
- новости из интернета.

Он получает только компактные рыночные признаки по уже разрешенным парам и выбирает, за какими из них смотреть в этом цикле.

## Где смотреть dry-run результаты

По умолчанию worker пишет в:

```text
data/dry-run/portfolio-state.json
data/dry-run/events.jsonl
```

`portfolio-state.json` - текущее виртуальное состояние портфеля после последнего цикла.

Пример:

```json
{
  "updatedAt": "2026-07-02T22:00:00Z",
  "cashEur": 22,
  "positions": [
    {
      "pair": "SOL/EUR",
      "side": "LONG",
      "quantity": 0.0421407473,
      "entryPrice": 71.19,
      "entryNotionalEur": 3,
      "lastPrice": 71.19,
      "marketValueEur": 3,
      "unrealizedPnlEur": 0,
      "unrealizedPnlPercent": 0
    }
  ]
}
```

`events.jsonl` - журнал всех циклов. Одна строка = один JSON record одного цикла. Там есть:

- `cycleId`;
- `activePairs`;
- `portfolioBefore`;
- `portfolioAfter`;
- список `decisions`;
- для каждого решения `score`, indicators, risk reasons, `dryRunAction`.

Быстро посмотреть ночные действия:

```bash
grep -E '"action":"WOULD_BUY"|"action":"WOULD_SELL"|"action":"WOULD_BUY_BLOCKED"' data/dry-run/events.jsonl
```

Посмотреть последний портфель:

```bash
cat data/dry-run/portfolio-state.json
```

## Как запустить на ночь

Например, цикл каждые 5 минут на публичных данных Kraken:

```bash
TRADINGBOT_MARKET_DATA_MODE=kraken \
TRADINGBOT_RUN_ONCE=false \
TRADINGBOT_LOOP_INTERVAL_SECONDS=300 \
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

Остановить: `Ctrl+C`.

Утром смотри:

```bash
cat data/dry-run/portfolio-state.json
grep -E '"action":"WOULD_BUY"|"action":"WOULD_SELL"|"action":"WOULD_BUY_BLOCKED"' data/dry-run/events.jsonl
```

## Что AI должен получать позже

Когда добавим AI risk advisor, payload должен быть другим и отдельным от watchlist advisor.

Например:

```json
{
  "pair": "UNI/EUR",
  "market": {
    "lastPrice": 2.76,
    "changePercent": 1.2,
    "volatilityPercent": 0.4,
    "technicalDesired": "LONG_MICRO"
  },
  "position": {
    "side": "LONG",
    "quantity": 1.08,
    "entryPrice": 2.76,
    "entryNotionalEur": 3,
    "unrealizedPnlPercent": 3.15
  },
  "newsContext": [
    {
      "source": "provider",
      "publishedAt": "2026-07-02T10:15:00Z",
      "headline": "..."
    }
  ],
  "allowedOutput": {
    "sentiment": "positive | neutral | negative",
    "eventRisk": "low | medium | high",
    "confidence": "0..1",
    "riskFlags": ["..."],
    "explanation": "..."
  }
}
```

И даже тогда AI output должен быть не командой, а входом в deterministic rules:

```text
AI eventRisk HIGH + current LONG -> desired NONE by rule AI_HIGH_RISK_EXIT
AI eventRisk HIGH + current NONE -> block new entry
AI stale/invalid -> ignore AI and audit warning
```

## Как понять, что бот "хотел бы купить"

Ищи блок с:

```text
desired=LONG_MICRO
risk=APPROVED
execution=WOULD_BUY
```

Это означает:

1. техническая стратегия дала вход;
2. risk gate не заблокировал;
3. dry-run открыл виртуальную позицию и записал это в журнал;
4. реальный ордер не отправлен.

## Как понять, что бот просто держится в стороне

Ищи:

```text
desired=NONE
execution=NO_ORDER
```

Это нормальное состояние. Большинство циклов должны быть именно такими, иначе стратегия слишком агрессивная.

## Какие настройки чаще всего менять

Файл:

```text
src/TradingBot.Worker/appsettings.json
```

Настройки:

```json
"Trading": {
  "LiveTradingEnabled": false,
  "TimeframeMinutes": 5,
  "MaxActiveInstruments": 2,
  "TargetOrderEur": 3
},
"Risk": {
  "MaxOrderEur": 3,
  "MaxDailyLossEur": 10,
  "MaxOpenPositions": 1,
  "KillSwitch": false
},
"Strategy": {
  "FastEmaPeriod": 9,
  "SlowEmaPeriod": 21,
  "RsiPeriod": 14,
  "MinimumLongScore": 0.55
}
```

Практически:

- `TimeframeMinutes` - интервал свечей.
- `MaxActiveInstruments` - сколько пар выбрать из candidate universe.
- `TargetOrderEur` - желаемый размер микропозиции.
- `MaxOrderEur` - жесткий risk cap на размер ордера.
- `KillSwitch=true` - принудительно блокирует новые risk-increasing действия.
- `MinimumLongScore` - чем выше, тем реже будет `LONG_MICRO`.

## Реальные ордера на Kraken (validate → live)

Лесенка из трёх безопасных стадий. Флаг `validate` вычисляется из конфига автоматически — вручную его не трогаешь.

```text
validate = НЕ (LiveTradingEnabled И kill-switch выключен И заданы ключи Kraken И режим kraken)
```

По умолчанию всегда безопасный путь. Ордер уходит на биржу только для решений `WOULD_BUY` / `WOULD_SELL` (то есть уже прошедших risk gate).

### Стадия 1 — dry-run (ключи не нужны)

```bash
TRADINGBOT_MARKET_DATA_MODE=kraken \
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

В консоли: `broker=disabled ...`. Только виртуальный портфель.

### Стадия 2 — validate=true (биржа проверяет ордер, но НЕ исполняет)

Создай API-ключ Kraken с правами **Query Funds + Create/Modify Orders** и **БЕЗ вывода средств**. Пропиши его в `appsettings.json` (`Kraken.ApiKey`/`ApiSecret`) или локально через env:

```bash
TRADINGBOT_MARKET_DATA_MODE=kraken \
TRADINGBOT_KRAKEN_API_KEY=... \
TRADINGBOT_KRAKEN_API_SECRET=... \
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

Что увидишь:

```text
broker=kraken-private mode=validate-only (validate=true, no execution)
broker-balance: EUR 50.0000 (auth OK, N assets)
...
  execution=WOULD_BUY
  broker=VALIDATED_OK side=buy vol=17.05 descr="buy 17.05 XLMEUR @ market"
```

- `broker-balance: EUR ...` подтверждает, что авторизация (ключ/подпись/nonce) работает и показывает реальный баланс.
- `broker=VALIDATED_OK` — сам Kraken подтвердил, что ордер валиден. Денег не тратится.
- `broker=VALIDATE_REJECTED: ...` — биржа отклонила (например, объём ниже `ordermin` или неверная точность).
- `broker=SKIPPED: ...` — не отправляли (например, объём ниже минимума пары).

### Стадия 3 — live (реальные микро-ордера)

Только когда стадия 2 отработала чисто. Один флаг в `appsettings.json`: `"Trading": { "LiveTradingEnabled": true }` (или env `TRADINGBOT_LIVE_TRADING_ENABLED=true`).

```bash
TRADINGBOT_MARKET_DATA_MODE=kraken \
TRADINGBOT_KRAKEN_API_KEY=... TRADINGBOT_KRAKEN_API_SECRET=... \
TRADINGBOT_LIVE_TRADING_ENABLED=true \
dotnet run --project src/TradingBot.Worker/TradingBot.Worker.csproj
```

На старте будет громкое предупреждение, а по сделкам:

```text
broker=LIVE_SUBMITTED side=buy vol=17.05 txid=OABCDE-...
```

Защиты перед реальным ордером: `LiveTradingEnabled=true`, kill-switch выключен, решение прошло risk gate, notional ≤ `MaxOrderEur`, объём ≥ `ordermin` и округлён по `lot_decimals` пары.

Важно на первом live-запуске: виртуальный dry-run портфель всё ещё считается параллельно с реальным ордером, поэтому ориентируйся на реальный `broker-balance`, а не на виртуальный. Полная реконструкция позиции из `Balance`/`TradesHistory` — следующий шаг (Plan 01). Рекомендуется `rm -rf data/dry-run` при переходе на live, чтобы виртуальное состояние не путало картину.

## Чего сейчас еще нет

- Есть private Kraken API (auth, `Balance`, `AddOrder` с `validate`), но **нет** персистентности nonce между рестартами (для одного worker'а ок).
- Нет реконструкции реальной позиции из `Balance`/`TradesHistory` (в live виртуальный портфель считается параллельно).
- Нет обработки 429/backoff и авто-kill-switch на серии ошибок брокера.
- Нет PostgreSQL.
- Нет audit journal в базе (пока файловый `events.jsonl`).
- Нет replay.

Следующий практический шаг: Plan 01 — soak в `validate=true` ≥24ч, затем первый реальный €2-ордер, персистентность nonce, реконструкция позиции из реального баланса и error-taxonomy брокера.
