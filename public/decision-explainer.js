(function (root) {
  "use strict";

  const TEXT = {
    SHORT_EMA_NOT_CONFIRMED: "Медвежья EMA ещё слишком слабая",
    SHORT_SCORE_BELOW_SIGNAL_THRESHOLD: "SHORT score ниже входного порога",
    SHORT_DOWNSIDE_CONFIRMATION_MISSING: "Нет подтверждения продолжения падения",
    SHORT_RANGE_UNAVAILABLE: "Не удалось надёжно оценить диапазон для SHORT",
    SHORT_PULLBACK_FROM_24H_HIGH_TOO_SMALL: "Цена ещё не отошла от 24h high",
    SHORT_FALLING_SNAPSHOTS_NOT_CONFIRMED: "Свежие цены не подтверждают падение",
    SHORT_SLOPE_NOT_NEGATIVE: "Краткосрочный наклон цены не направлен вниз",
    SHORT_FRESH_TAPE_NOT_CONFIRMED: "Падение не подтверждено одновременно snapshots и свечами",
    SHORT_ENTRY_TOO_CLOSE_TO_LOCAL_LOW: "Слишком поздно шортить возле локального low",
    SHORT_ENTRY_DRIFT_TOO_HIGH: "Цена уже слишком далеко упала после сигнала",
    LONG_24H_RANGE_UNAVAILABLE: "Не удалось надёжно оценить диапазон для LONG",
    LONG_24H_RANGE_TOO_NARROW: "24h диапазон слишком узкий для надёжного LONG",
    LONG_24H_RANGE_POSITION_TOO_HIGH: "Цена слишком высоко в 24h диапазоне",
    LONG_EMA_NOT_CONFIRMED: "LONG EMA ещё слишком слабая",
    LONG_REBOUND_FROM_24H_LOW_TOO_SMALL: "Отскок от 24h low ещё не подтверждён",
    LONG_RISING_SNAPSHOTS_NOT_CONFIRMED: "Свежие цены не подтверждают рост",
    LONG_SHORT_SLOPE_NOT_POSITIVE: "Краткосрочный наклон цены не направлен вверх",
    LONG_FRESH_TAPE_NOT_CONFIRMED: "Рост не подтверждён одновременно snapshots и свечами",
    LONG_ENTRY_TOO_CLOSE_TO_LOCAL_HIGH: "Слишком поздно покупать возле локального high",
    LONG_ENTRY_DRIFT_TOO_HIGH: "Цена уже слишком далеко выросла после сигнала",
    LONG_UPPER_RANGE_FRESH_TAPE_NOT_ENOUGH: "Верх диапазона: одной свежей ленты мало, нужен подтверждённый пробой",
    LONG_LOW_RANGE_STRONG_CONFIRMATION_MISSING: "Отскок не подтверждён: нет ни свежей ленты, ни моментума по свечам",
    ENTRY_STALE_NEAR_HIGH: "Сигнал устарел: цена уже ушла к локальному high",
    REJECT_ENTRY_STALE_NEAR_HIGH: "Сигнал устарел: цена уже ушла к локальному high",
    REJECT_FUTURES_RISK: "Futures-вход отклонён risk-проверкой",
    REJECT_LIQUIDATION_DISTANCE: "Ликвидация слишком близко для этого плеча",
    REJECT_MARGIN_UTILIZATION: "Не хватает свободной маржи: лимит утилизации счёта",
    REJECT_OPEN_RISK_CAP: "Суммарный открытый риск превысил бы лимит",
    REJECT_FUNDING_ADVERSE: "Funding-ставка невыгодна для этого направления",
    REJECT_EXIT_DEPTH: "Не хватает глубины стакана на выход из позиции",
    REJECT_DUPLICATE_ENTRY_PENDING: "По этой паре ещё не подтверждён предыдущий ордер",
    ENTRY_BLACKOUT: "Сейчас действует временный запрет новых входов",
    ENTRY_INVALID_SPREAD: "Bid/ask отсутствует или некорректен — spread не проверить",
    ENTRY_INVALID_REFERENCE_PRICE: "Нет свежей цены для безопасного входа",
    ENTRY_ATR_MISSING: "Не удалось посчитать ATR — волатильность неизвестна",
    ENTRY_ATR_STALE: "Последняя свеча устарела для этого таймфрейма",
    ENTRY_COST_INVALID: "Не удалось оценить издержки полного круга сделки",
    ENTRY_EXITS_INVALID: "Не удалось рассчитать корректные уровни выхода",
    ENTRY_VOLUME: "Суточный объём ниже минимума",
    ENTRY_DEPTH: "Недостаточно глубины стакана для входа",
    ENTRY_DEPTH_MISSING: "Нет данных о глубине стакана",
    ENTRY_EXIT_DEPTH: "Не хватает глубины на аварийный выход из позиции",
    ENTRY_OPEN_RISK_CAP: "Суммарный открытый риск превысил бы лимит",
    ENTRY_OPEN_RISK_UNSAFE: "Открытый риск нельзя посчитать безопасно",
    MAX_POSITIONS: "Все слоты позиций уже заняты",
    CYCLE_POSITION_LIMIT: "В этом цикле уже открыт допустимый максимум позиций",
    EXPLORATORY_RANK: "Кандидат не вошёл в допустимый exploratory rank",
    EARLY_ENTRY_RANK: "Ранний сигнал недостаточно высоко в рейтинге кандидатов",
    MARKET_REGIME: "Рыночный режим не разрешает этот вход",
    LIVE_FALLBACK_REJECTED_RISK: "Резервный taker-ордер отклонён risk-проверкой",
    LIVE_FALLBACK_REJECTED_SLIPPAGE: "Резервный taker-ордер отклонён: проскальзывание выше лимита",
    LIVE_FALLBACK_REJECTED_SPREAD: "Резервный taker-ордер отклонён: spread слишком широкий",
    LIVE_FALLBACK_REJECTED_STALE_QUOTE: "Резервный taker-ордер отклонён: котировка устарела",
    REJECT_SCORE_BELOW_THRESHOLD: "Score ниже входного порога",
    REJECT_SPREAD_TOO_WIDE: "Spread слишком широкий",
    REJECT_NEGATIVE_RECENT_PRICE_ACTION: "Свежая цена движется против LONG",
    REJECT_NO_VOLUME_CONFIRMATION: "Объём не подтвердил движение",
    REJECT_NO_MOMENTUM_CONFIRMATION: "Momentum не подтвердил движение",
    REJECT_NO_DIRECTIONAL_SCORE: "Ни LONG, ни SHORT score не прошли входной порог",
    REJECT_PRICE_EXTENDED: "Цена уже слишком далеко ушла от точки сигнала",
    REJECT_ALREADY_HOLDING: "Позиция по этой паре уже открыта",
    REJECT_ACTIVE_PAIR_FILTER: "Пара не вошла в активный набор этого цикла",
    REJECT_SIGNAL_DECAY: "Сигнал ослаб до момента исполнения",
    REJECT_LOW_LIQUIDITY: "Недостаточно ликвидности для безопасного входа",
    REJECT_NO_BULLISH_SIGNAL: "Нет подтверждённого LONG-сигнала",
    REJECT_NO_FUTURES_SIGNAL: "Нет подтверждённого futures-сигнала",
    REJECT_PAIR_UNAVAILABLE: "Пара сейчас недоступна для торговли",
    REJECT_EXPLORATORY_RANK: "Кандидат не вошёл в допустимый exploratory rank",
    REJECT_EXPLORATORY_REQUIRES_POSITIVE_PRICE_ACTION: "Exploratory-вход не получил подтверждения свежей ценой",
    REJECT_EARLY_ENTRY_RANK: "Ранний сигнал недостаточно высоко в рейтинге кандидатов",
    REJECT_COOLDOWN: "После прошлой сделки ещё действует cooldown",
    REJECT_MAX_POSITIONS: "Все слоты позиций уже заняты",
    REJECT_DAILY_RISK: "Достигнут дневной лимит риска",
    REJECT_NO_CAPACITY: "Недостаточно свободной ёмкости портфеля",
    REJECT_RISK_LIMITS: "Сделка превышает лимит риска",
    REJECT_CYCLE_POSITION_LIMIT: "В этом цикле уже открыт допустимый максимум позиций",
    REJECT_ENTRY_BLACKOUT: "Сейчас действует временный запрет новых входов",
    REJECT_CORRELATION_LIMIT: "Лимит коррелированной экспозиции достигнут",
    REJECT_INVALID_MARKET_DATA: "Рыночные данные неполные или некорректные",
    REJECT_INSUFFICIENT_PRICE_HISTORY: "Недостаточно истории цены",
    REJECT_PRICE_ACTION_UNKNOWN: "Недостаточно свежих snapshots для проверки движения",
    REJECT_MARKET_REGIME: "Рыночный режим не разрешает этот вход",
    REJECT_FRICTION_TOO_HIGH: "Комиссии, spread или slippage делают вход невыгодным",
    REJECT_ENTRY_PRICE_DEVIATION: "Цена исполнения слишком далеко ушла от сигнала",
    DESIRED_LONG: "Открытая LONG-позиция всё ещё соответствует сигналу",
    MIN_HOLD_BLOCK: "Разворот сигнала есть, но минимальное время удержания ещё не прошло",
    MIN_PROFIT_BLOCK: "Выход по сигналу заблокирован минимальным требованием к прибыли",
    FLIP_LOSS_FLOOR_BLOCK: "Разворот подтверждён, но текущий убыток ниже разрешённой границы выхода",
    COOLDOWN_BLOCK: "После прошлой сделки ещё действует cooldown",
    STOPLOSS_COOLDOWN: "После stop-loss действует усиленный cooldown",
    HOURLY_ENTRY_LIMIT: "Достигнут часовой лимит новых входов",
    DAILY_LOSS_BLOCK: "Достигнут дневной лимит убытка",
    FRICTION_BLOCK: "Торговые издержки слишком велики",
    CASH_RESERVE_BLOCK: "Вход нарушил бы обязательный резерв свободных средств",
    LIVE_BROKER_UNAVAILABLE: "Live broker недоступен — ордер не отправлен",
    LIVE_ORDER_FAILED: "Live-ордер не был исполнен",
    LIVE_ORDER_REJECTED: "Kraken отклонил ордер",
    LIVE_ORDER_SIZE_TOO_SMALL: "Размер ордера меньше допустимой точности Kraken",
    ENTRY_INVALID_REFERENCE_PRICE: "Нет свежей цены для безопасного входа",
    LIVE_ENTRY_PRICE_DEVIATION: "Цена на Kraken ушла дальше допустимого лимита",
    LIVE_LEVERAGE_SET_FAILED: "Не удалось безопасно установить leverage",
    FILL_RECONCILIATION_PENDING: "Ордер принят, но подтверждение fill ещё ожидается",
    CORRELATION_GROUP_LIMIT: "В корреляционной группе уже максимум позиций",
    CORRELATION_EXPOSURE_LIMIT: "Лимит экспозиции корреляционной группы превышен",
    HIGH_BETA_LIMIT: "Лимит high-beta позиций достигнут",
    EXTERNAL_SIGNAL_FLIP_BLOCK: "Внешняя позиция: сигнал не имеет права её закрыть",
    TRAILING_ACTIVATED: "Рабочий TP достигнут — включён trailing stop",
    TRAILING_ACTIVATION_FAILED: "Рабочий TP достигнут, но trailing stop не установился",
    SELL_STOP_LOSS: "Позиция закрыта по рабочему stop-loss",
    SELL_TAKE_PROFIT: "Позиция закрыта по take-profit",
    SELL_MAX_HOLD: "Позиция закрыта правилом максимального удержания",
    SELL_SIGNAL_FLIP: "Позиция закрыта после разворота сигнала",
    SELL_TRAILING_STOP: "Позиция закрыта trailing stop",
    SELL_SCORE_DECAY: "Позиция закрыта после устойчивого ослабления score",
    SELL_POST_ENTRY_ADVERSE: "Позиция закрыта: сразу после входа рынок пошёл против неё",
    SELL_KILL_SWITCH: "Позиция закрыта аварийным kill switch",
    SELL_EMERGENCY_RISK: "Позиция закрыта аварийным risk-правилом",
    SELL_BROKER_SAFETY: "Позиция закрыта защитным правилом broker safety",
    MAX_HOLD_HEALTHY_HOLD: "Время удержания истекло, но позиция в плюсе — оставлена открытой",
    MAX_MARGIN: "Размер ограничен лимитом маржи на позицию",
    MAX_NOTIONAL: "Размер ограничен лимитом ноционала на позицию"
  };

  // Entry channel that admitted a position, recorded on every open/close row.
  const CHANNEL = {
    Standard: "Обычный вход",
    Continuation: "Продолжение движения",
    Breakout: "Подтверждённый пробой",
    DipBounce: "Отскок от низа диапазона"
  };

  const first = (...values) => values.find(value => value !== null && value !== undefined && String(value).trim() !== "");
  const number = value => {
    const parsed = Number(value);
    return Number.isFinite(parsed) ? parsed : null;
  };
  const fmt = (value, digits = 2) => number(value) === null ? "—" : number(value).toFixed(digits);
  const pct = value => number(value) === null ? "—" : `${number(value) >= 0 ? "+" : ""}${fmt(value, 3)}%`;
  const getAction = decision => decision.dryRunAction || decision.DryRunAction || {};
  const contributions = decision => Array.isArray(decision.contributions) ? decision.contributions : [];
  const contribution = (decision, name) => contributions(decision).find(item => String(item.name || "").toLowerCase() === name.toLowerCase());
  const contributionReason = (decision, name) => contribution(decision, name)?.reason || "";

  function reasonParts(decision) {
    const action = getAction(decision);
    const values = [
      action.reason, decision.reason, action.holdReason, action.entryRejectionReason,
      decision.entryRejectionReason, action.entryFreshnessBlockReason,
      action.shortEntryBlockReasonCode, action.longRangeBlockReasonCode,
      action.correlationRejectedReason, action.fundingState, action.shortAllowed,
      decision.shortBaseBlockReason
    ];
    for (const item of decision.riskReasons || []) values.push(item);
    return [...new Set(values.filter(Boolean).map(value => String(value).trim()))];
  }

  function inferShortFields(decision) {
    const shortReason = contributionReason(decision, "ShortScore");
    const emaReason = contributionReason(decision, "EMA");
    const parsedShort = /short diagnostic score\s+([0-9.]+)/i.exec(shortReason);
    const parsedBearGap = /fast EMA is below slow EMA by\s+([0-9.]+)%/i.exec(emaReason);
    return {
      shortScore: number(first(decision.shortScore, parsedShort?.[1])),
      bearishGap: number(first(decision.bearishEmaGapPercent, parsedBearGap?.[1])),
      hasBearish: decision.hasBearishStructure === true || /below slow/i.test(emaReason),
      allowsShort: decision.allowsShort === true
    };
  }

  // Entry thresholds per direction. A threshold is only usable when it is a positive
  // number: printing "below threshold 0.00" next to a score of 0.70 is worse than
  // printing no threshold at all.
  const longThresholdOf = decision => {
    const value = number(decision.longScoreThreshold);
    return value !== null && value > 0 ? value : null;
  };
  const shortThresholdOf = decision => {
    const value = number(first(decision.shortScoreThreshold, decision.longScoreThreshold));
    return value !== null && value > 0 ? value : null;
  };
  const belowThresholdText = (score, threshold) =>
    threshold !== null && number(score) !== null && number(score) < threshold
      ? `${fmt(score)} ниже порога ${fmt(threshold)}`
      : `${fmt(score)} не прошёл входной порог`;

  // Both-direction verdict for a cycle that opened nothing. This is the answer to
  // "does it even consider LONG?": both scores and both thresholds, always.
  function bothSidesText(decision) {
    const short = inferShortFields(decision);
    const longPart = `LONG ${belowThresholdText(decision.score, longThresholdOf(decision))}`;
    return short.shortScore === null
      ? longPart
      : `${longPart}; SHORT ${belowThresholdText(short.shortScore, shortThresholdOf(decision))}`;
  }

  function candidateSide(decision) {
    const action = getAction(decision);
    const explicit = String(first(action.side, decision.side, decision.desiredPosition, action.desiredPosition, "")).toUpperCase();
    if (explicit.includes("SHORT")) return "SHORT";
    if (explicit.includes("LONG")) return "LONG";
    const short = inferShortFields(decision);
    if (short.hasBearish) return "SHORT";
    if (decision.hasBullishStructure) return "LONG";
    return "NONE";
  }

  // Codes come from several layers and the raw one is not always the most specific:
  // a portfolio hold code (ENTRY_VOLUME) and its mapped rejection (REJECT_LOW_LIQUIDITY)
  // can both be present. Prefer the first candidate that actually has a translation, so
  // a raw low-level code never wins over an explained one; fall back to the first
  // non-empty candidate when nothing is translated yet.
  function explicitCode(decision) {
    const action = getAction(decision);
    const candidates = [
      action.exitReasonCode,
      action.holdReasonCode,
      action.shortEntryBlockReasonCode,
      action.longRangeBlockReasonCode,
      decision.shortBaseBlockReasonCode,
      action.entryRejectionReason,
      decision.entryRejectionReason,
      action.correlationRejectedReason
    ].filter(value => value !== null && value !== undefined && String(value).trim() !== "")
      .map(value => String(value).trim());

    return candidates.find(candidate => TEXT[candidate]) || candidates[0] || null;
  }

  // Last-resort readable label for a code that has no translation yet, so the page
  // shows "Long foo bar" instead of a bare LONG_FOO_BAR token.
  function humanize(code) {
    const text = String(code || "").replace(/^REJECT_/, "").replace(/_/g, " ").trim().toLowerCase();
    return text === "" ? "" : text.charAt(0).toUpperCase() + text.slice(1);
  }

  function fallbackCode(decision) {
    const action = getAction(decision);
    const actionName = String(first(action.action, decision.action, "NO_ORDER")).toUpperCase();
    const all = reasonParts(decision).join(" | ").toLowerCase();
    const side = candidateSide(decision);
    const short = inferShortFields(decision);
    const bullishGap = number(decision.bullishEmaGapPercent);
    const emaMin = number(decision.minimumEmaGapPercent);
    const longThreshold = number(decision.longScoreThreshold);
    const shortThreshold = number(first(decision.shortScoreThreshold, decision.longScoreThreshold, 0.85));

    if (/holding existing exposure|already holding|existing exposure/.test(all)) return "REJECT_ALREADY_HOLDING";
    if (actionName === "NO_ORDER" && side === "SHORT") {
      if (emaMin !== null && short.bearishGap !== null && short.bearishGap < emaMin) return "SHORT_EMA_NOT_CONFIRMED";
      if (short.shortScore !== null && shortThreshold !== null && short.shortScore < shortThreshold) return "SHORT_SCORE_BELOW_SIGNAL_THRESHOLD";
      if (!short.allowsShort) return "SHORT_DOWNSIDE_CONFIRMATION_MISSING";
    }
    if (actionName === "NO_ORDER" && side === "LONG") {
      if (emaMin !== null && bullishGap !== null && bullishGap < emaMin) return "LONG_EMA_NOT_CONFIRMED";
      if (number(decision.score) !== null && longThreshold !== null && number(decision.score) < longThreshold) return "REJECT_SCORE_BELOW_THRESHOLD";
      if (String(decision.priceActionDirection || "").includes("FALLING")) return "REJECT_NEGATIVE_RECENT_PRICE_ACTION";
    }
    if (actionName === "NO_ORDER" && side === "NONE") {
      const longScore = number(decision.score);
      if (longScore !== null && longThreshold !== null && longScore < longThreshold
        && short.shortScore !== null && shortThreshold !== null && short.shortScore < shortThreshold) {
        return "REJECT_NO_DIRECTIONAL_SCORE";
      }
      if (longScore !== null && longThreshold !== null && longScore < longThreshold) return "REJECT_SCORE_BELOW_THRESHOLD";
      if (short.shortScore !== null && shortThreshold !== null && short.shortScore < shortThreshold) return "SHORT_SCORE_BELOW_SIGNAL_THRESHOLD";
    }
    if (/falling snapshots/.test(all)) return "SHORT_FALLING_SNAPSHOTS_NOT_CONFIRMED";
    if (/rising snapshots/.test(all)) return "LONG_RISING_SNAPSHOTS_NOT_CONFIRMED";
    if (/local low/.test(all)) return "SHORT_ENTRY_TOO_CLOSE_TO_LOCAL_LOW";
    if (/local high/.test(all)) return "LONG_ENTRY_TOO_CLOSE_TO_LOCAL_HIGH";
    // Specific futures risk blocks. These must be matched BEFORE the broad
    // margin/notional/open-risk pattern below, which would otherwise swallow them
    // into a generic "risk limits" message.
    if (/liquidation distance/.test(all)) return "REJECT_LIQUIDATION_DISTANCE";
    if (/margin utilization/.test(all)) return "REJECT_MARGIN_UTILIZATION";
    if (/open risk/.test(all)) return "REJECT_OPEN_RISK_CAP";
    if (/funding rate/.test(all)) return "REJECT_FUNDING_ADVERSE";
    if (/exit depth/.test(all)) return "REJECT_EXIT_DEPTH";
    if (/quote volume/.test(all)) return "REJECT_LOW_LIQUIDITY";
    if (/fill reconciliation pending/.test(all)) return "REJECT_DUPLICATE_ENTRY_PENDING";
    if (/entry blackout/.test(all)) return "REJECT_ENTRY_BLACKOUT";
    // Short-gate texts. These must precede the generic "score ... below" pattern, which
    // would otherwise report them as a plain long-score rejection.
    if (/short score\s+[\d.]+\s+(below|is below)/.test(all)) return "SHORT_SCORE_BELOW_SIGNAL_THRESHOLD";
    if (/bearish signal not confirmed|none of downside momentum/.test(all)) return "SHORT_DOWNSIDE_CONFIRMATION_MISSING";
    if (/allowslongs=|allowsshorts=/.test(all)) return "REJECT_MARKET_REGIME";
    if (/spread/.test(all)) return "REJECT_SPREAD_TOO_WIDE";
    if (/position slots|max futures positions/.test(all)) return "REJECT_MAX_POSITIONS";
    if (/correlation/.test(all)) return "REJECT_CORRELATION_LIMIT";
    if (/funding/.test(all)) return "REJECT_MARKET_REGIME";
    if (/margin|notional|open risk|daily loss/.test(all)) return "REJECT_RISK_LIMITS";
    if (/price deviation|deviation from signal/.test(all)) return "REJECT_ENTRY_PRICE_DEVIATION";
    if (/cooldown/.test(all)) return "REJECT_COOLDOWN";
    if (/score .*below|below required|long threshold/.test(all)) return "REJECT_SCORE_BELOW_THRESHOLD";
    if (/ema gap/.test(all)) return side === "SHORT" ? "SHORT_EMA_NOT_CONFIRMED" : "REJECT_NO_BULLISH_SIGNAL";
    return null;
  }

  // Catch-all codes the workers emit when no specific hold code was set. They are
  // translated (so nothing renders raw) but they say nothing on their own, so a
  // concrete reason derived from the gate text must always win over them.
  const GENERIC = new Set(["REJECT_NO_FUTURES_SIGNAL", "REJECT_FUTURES_RISK", "REJECT_RISK_LIMITS"]);

  function codeOf(decision) {
    const code = explicitCode(decision);
    const fallback = fallbackCode(decision);
    if (code && GENERIC.has(code) && fallback && !GENERIC.has(fallback)) return fallback;
    if (code && TEXT[code]) return code;
    return fallback || (code ? String(code) : null);
  }

  // First gate reason that is real prose rather than a bare CODE_TOKEN, used to give a
  // generic verdict some substance instead of "отклонён risk-проверкой" and nothing else.
  function concreteReason(decision) {
    return reasonParts(decision).find(part => !/^[A-Z][A-Z0-9_]*$/.test(part.trim())) || null;
  }

  function summary(decision, code) {
    const action = getAction(decision);
    const actionName = String(first(action.action, decision.action, "NO_ORDER")).toUpperCase();
    const pair = first(decision.pair, action.pair, "эта пара");
    const side = candidateSide(decision);
    const short = inferShortFields(decision);
    const threshold = number(first(decision.longScoreThreshold, decision.shortScoreThreshold));

    if (/OPEN|BUY/.test(actionName)) return `Вход ${side === "SHORT" ? "в SHORT" : "в LONG"} разрешён: сигнал и все последующие проверки пройдены.`;
    if (/CLOSE|SELL/.test(actionName)) return TEXT[code] || `Позиция ${pair} закрыта правилом выхода.`;
    if (/HOLD/.test(actionName)) return TEXT[code] || `Позиция ${pair} остаётся открытой; нового ордера нет.`;
    if (code === "SHORT_EMA_NOT_CONFIRMED") return `SHORT не рассматривался дальше: расхождение EMA вниз ${pct(short.bearishGap)} меньше обязательного ${pct(decision.minimumEmaGapPercent)}.`;
    if (code === "LONG_EMA_NOT_CONFIRMED") return `LONG не рассматривался дальше: score прошёл, но EMA gap вверх ${pct(decision.bullishEmaGapPercent)} меньше обязательного ${pct(decision.minimumEmaGapPercent)}.`;
    if (code === "REJECT_NO_DIRECTIONAL_SCORE") return `Нет направления для входа: ${bothSidesText(decision)}.`;
    if (code === "REJECT_ALREADY_HOLDING") return `По ${pair} уже есть открытая позиция, поэтому бот не добавляет второй вход. Дальше эту позицию ведут TP/SL, trailing или разрешённые правила выхода.`;
    if (code === "SHORT_SCORE_BELOW_SIGNAL_THRESHOLD") return `Ни одно направление не прошло: ${bothSidesText(decision)}.`;
    if (code === "SHORT_DOWNSIDE_CONFIRMATION_MISSING") return `Bearish EMA была, но не хватило подтверждения вниз: нужен хотя бы один из факторов — candle momentum, повышенный объём или цена ниже trend MA.`;
    // A generic verdict on its own explains nothing: always show which gate spoke.
    if (code && GENERIC.has(code)) {
      const detail = concreteReason(decision);
      return detail
        ? `${TEXT[code] || humanize(code)}: ${detail}`
        : `${TEXT[code] || humanize(code)}. Конкретная причина не записана в решении.`;
    }
    if (code && TEXT[code]) return `${TEXT[code]}. Поэтому новый ордер не отправлен.`;
    if (code) return `${humanize(code)}. Поэтому новый ордер не отправлен.`;
    return side === "NONE" ? "Нет подтверждённого направления для нового входа." : `Кандидат ${side} остановлен до отправки ордера.`;
  }

  function facts(decision) {
    const action = getAction(decision);
    const side = candidateSide(decision);
    const short = inferShortFields(decision);
    const items = [];
    const add = (label, value, state = "neutral", detail = "") => {
      if (value === null || value === undefined || value === "") return;
      items.push({ label, value: String(value), state, detail });
    };
    add("Кандидат", side === "NONE" ? "нет" : side, side === "NONE" ? "neutral" : "info");
    // Always show BOTH directions with their thresholds, so a bearish tape can never
    // look like the bot only ever thought about SHORT.
    const longThreshold = longThresholdOf(decision);
    const shortThreshold = shortThresholdOf(decision);
    add(
      "LONG score",
      longThreshold === null ? fmt(decision.score) : `${fmt(decision.score)} / порог ${fmt(longThreshold)}`,
      longThreshold !== null && number(decision.score) >= longThreshold ? "pass" : "neutral");
    if (short.hasBearish || short.shortScore !== null) {
      add(
        "SHORT score",
        shortThreshold === null ? fmt(short.shortScore) : `${fmt(short.shortScore)} / порог ${fmt(shortThreshold)}`,
        short.allowsShort ? "pass" : "fail");
    }
    if (short.bearishGap !== null) add("EMA вниз", pct(short.bearishGap), number(decision.minimumEmaGapPercent) !== null && short.bearishGap < number(decision.minimumEmaGapPercent) ? "fail" : "pass");
    const spread = number(first(decision.spreadPercent, action.spreadPercent));
    if (spread !== null) add("Spread", pct(spread), "neutral");
    const pa = first(decision.priceActionDirection, action.priceActionDirection);
    if (pa) add("Свежая цена", `${pa} ${pct(first(decision.priceActionTrendPercent, action.priceActionTrendPercent))}`, side === "SHORT" && String(pa).includes("RISING") ? "warn" : "neutral");
    const range = number(first(action.longRange24hPosition, action.closePercentile));
    if (range !== null) add("Позиция в диапазоне", pct(range), "neutral");
    const channel = first(action.entryChannel, decision.entryChannel);
    if (channel) add("Канал входа", CHANNEL[channel] || channel, "info");
    return items;
  }

  function evidence(decision) {
    const rows = [];
    for (const item of contributions(decision)) {
      rows.push({ label: item.name || "signal", value: `${number(item.value) !== null ? `${number(item.value) >= 0 ? "+" : ""}${fmt(item.value)}` : ""} ${item.reason || ""}`.trim() });
    }
    for (const reason of reasonParts(decision)) rows.push({ label: "gate", value: reason });
    return rows;
  }

  function analyze(decision) {
    const code = codeOf(decision);
    return {
      code,
      headline: (code && TEXT[code]) || (code && humanize(code))
        || (candidateSide(decision) === "NONE" ? "Нет сигнала для входа" : "Вход остановлен проверкой"),
      summary: summary(decision, code),
      side: candidateSide(decision),
      facts: facts(decision),
      evidence: evidence(decision)
    };
  }

  root.DecisionExplainer = { analyze, codeOf, candidateSide, reasonParts, humanize, translations: TEXT, channels: CHANNEL };
})(typeof window !== "undefined" ? window : globalThis);
