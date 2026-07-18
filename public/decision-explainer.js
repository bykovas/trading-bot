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
    ENTRY_STALE_NEAR_HIGH: "Сигнал устарел: цена уже ушла к локальному high",
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
    SELL_BROKER_SAFETY: "Позиция закрыта защитным правилом broker safety"
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

  function explicitCode(decision) {
    const action = getAction(decision);
    return first(
      action.exitReasonCode,
      action.holdReasonCode,
      action.shortEntryBlockReasonCode,
      action.longRangeBlockReasonCode,
      decision.shortBaseBlockReasonCode,
      action.entryRejectionReason,
      decision.entryRejectionReason,
      action.correlationRejectedReason
    );
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

  function codeOf(decision) {
    const code = explicitCode(decision);
    const fallback = fallbackCode(decision);
    if (code === "REJECT_NO_FUTURES_SIGNAL" && fallback) return fallback;
    if (code && TEXT[code]) return code;
    return fallback || (code ? String(code) : null);
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
    if (code === "REJECT_NO_DIRECTIONAL_SCORE") return `Нет направления для входа: LONG score ${fmt(decision.score)} ниже порога ${fmt(decision.longScoreThreshold)}, и SHORT score ${fmt(short.shortScore)} ниже порога ${fmt(first(decision.shortScoreThreshold, decision.longScoreThreshold))}.`;
    if (code === "SHORT_SCORE_BELOW_SIGNAL_THRESHOLD") return `SHORT не рассматривался дальше: SHORT score ${fmt(short.shortScore)} ниже порога ${fmt(threshold)}. Показанный общий score к этому отказу не относится.`;
    if (code === "SHORT_DOWNSIDE_CONFIRMATION_MISSING") return `Bearish EMA была, но не хватило подтверждения вниз: нужен хотя бы один из факторов — candle momentum, повышенный объём или цена ниже trend MA.`;
    if (code && TEXT[code]) return `${TEXT[code]}. Поэтому новый ордер не отправлен.`;
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
    add("LONG score", fmt(decision.score), number(decision.score) >= number(decision.longScoreThreshold) ? "pass" : "neutral");
    if (short.hasBearish || short.shortScore !== null) add("SHORT score", fmt(short.shortScore), short.allowsShort ? "pass" : "fail");
    if (short.bearishGap !== null) add("EMA вниз", pct(short.bearishGap), number(decision.minimumEmaGapPercent) !== null && short.bearishGap < number(decision.minimumEmaGapPercent) ? "fail" : "pass");
    const spread = number(first(decision.spreadPercent, action.spreadPercent));
    if (spread !== null) add("Spread", pct(spread), "neutral");
    const pa = first(decision.priceActionDirection, action.priceActionDirection);
    if (pa) add("Свежая цена", `${pa} ${pct(first(decision.priceActionTrendPercent, action.priceActionTrendPercent))}`, side === "SHORT" && String(pa).includes("RISING") ? "warn" : "neutral");
    const range = number(first(action.longRange24hPosition, action.closePercentile));
    if (range !== null) add("Позиция в диапазоне", pct(range), "neutral");
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
      headline: (code && TEXT[code]) || (candidateSide(decision) === "NONE" ? "Нет сигнала для входа" : "Вход остановлен проверкой"),
      summary: summary(decision, code),
      side: candidateSide(decision),
      facts: facts(decision),
      evidence: evidence(decision)
    };
  }

  root.DecisionExplainer = { analyze, codeOf, candidateSide, reasonParts, translations: TEXT };
})(typeof window !== "undefined" ? window : globalThis);
