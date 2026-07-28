"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
require("../public/decision-explainer.js");

const analyze = globalThis.DecisionExplainer.analyze;
const translations = globalThis.DecisionExplainer.translations;

function decision(overrides = {}) {
  return {
    pair: "FARTCOIN/USD",
    score: 0.15,
    desiredPosition: "FLAT",
    spreadPercent: 0.076,
    priceActionDirection: "RISING",
    priceActionTrendPercent: 0.143,
    riskReasons: [],
    contributions: [],
    dryRunAction: { action: "NO_ORDER", ...overrides.dryRunAction },
    ...overrides
  };
}

{
  const result = analyze(decision({
    hasBearishStructure: true,
    allowsShort: false,
    bearishEmaGapPercent: 0.21,
    minimumEmaGapPercent: 0.35,
    shortScore: 0.7,
    longScoreThreshold: 0.85,
    shortBaseBlockReasonCode: "SHORT_EMA_NOT_CONFIRMED"
  }));
  assert.equal(result.code, "SHORT_EMA_NOT_CONFIRMED");
  assert.match(result.summary, /0\.210%/);
  assert.match(result.summary, /0\.350%/);
}

{
  const result = analyze(decision({
    hasBearishStructure: true,
    allowsShort: false,
    bearishEmaGapPercent: 0.5,
    minimumEmaGapPercent: 0.35,
    shortScore: 0.75,
    longScoreThreshold: 0.85,
    shortBaseBlockReasonCode: "SHORT_SCORE_BELOW_SIGNAL_THRESHOLD"
  }));
  assert.equal(result.code, "SHORT_SCORE_BELOW_SIGNAL_THRESHOLD");
  assert.match(result.summary, /SHORT score 0\.75/);
  assert.match(result.summary, /общий score к этому отказу не относится/);
}

{
  const result = analyze(decision({
    hasBearishStructure: true,
    allowsShort: false,
    bearishEmaGapPercent: 0.5,
    shortScore: 0.9,
    longScoreThreshold: 0.85,
    shortBaseBlockReasonCode: "SHORT_DOWNSIDE_CONFIRMATION_MISSING"
  }));
  assert.equal(result.code, "SHORT_DOWNSIDE_CONFIRMATION_MISSING");
  assert.match(result.summary, /candle momentum/);
  assert.match(result.summary, /trend MA/);
}

{
  const result = analyze(decision({
    desiredPosition: "SHORT",
    shortScore: 0.9,
    dryRunAction: {
      action: "NO_ORDER",
      side: "SHORT",
      holdReasonCode: "SHORT_ENTRY_TOO_CLOSE_TO_LOCAL_LOW"
    }
  }));
  assert.equal(result.code, "SHORT_ENTRY_TOO_CLOSE_TO_LOCAL_LOW");
  assert.match(result.headline, /локального low/);
}

{
  const result = analyze(decision({
    pair: "B3/USD",
    score: 0.8,
    longScoreThreshold: 0.8,
    desiredPosition: "FLAT",
    hasBullishStructure: true,
    bullishEmaGapPercent: 0.18315018315018315,
    minimumEmaGapPercent: 0.2,
    priceActionDirection: "FALLING",
    priceActionTrendPercent: -0.159,
    entryRejectionReason: "REJECT_NO_FUTURES_SIGNAL",
    riskReasons: ["no futures long: score 0.8 passed but EMA gap 0.183% is below required 0.2%"],
    contributions: [
      { name: "EMA", value: 0.25, reason: "fast EMA is above slow EMA by 0.183% but below configured minimum 0.2%; partial early-structure credit" }
    ]
  }));
  assert.equal(result.code, "LONG_EMA_NOT_CONFIRMED");
  assert.match(result.headline, /LONG EMA/);
  assert.match(result.summary, /0\.183%/);
  assert.match(result.summary, /0\.200%/);
}

{
  const result = analyze(decision({
    pair: "BEAM/USD",
    score: 0.5,
    shortScore: 0.3,
    longScoreThreshold: 0.8,
    shortScoreThreshold: 0.85,
    desiredPosition: "FLAT",
    hasBullishStructure: false,
    hasBearishStructure: false,
    bearishEmaGapPercent: 0.066,
    priceActionDirection: "FALLING",
    priceActionTrendPercent: -0.073,
    spreadPercent: 3.081,
    entryRejectionReason: "REJECT_NO_FUTURES_SIGNAL"
  }));
  assert.equal(result.code, "REJECT_NO_DIRECTIONAL_SCORE");
  assert.match(result.headline, /Ни LONG, ни SHORT/);
  assert.match(result.summary, /LONG score 0\.50/);
  assert.match(result.summary, /SHORT score 0\.30/);
}

{
  const result = analyze(decision({
    pair: "SYN/USD",
    score: 0.65,
    shortScore: 0.3,
    hasBullishStructure: true,
    bullishEmaGapPercent: 1.671,
    dryRunAction: {
      action: "NO_ORDER",
      side: "LONG"
    },
    riskReasons: ["holding existing exposure; TP/SL and reversal rules govern this pair"]
  }));
  assert.equal(result.code, "REJECT_ALREADY_HOLDING");
  assert.match(result.headline, /уже открыта/);
  assert.match(result.summary, /уже есть открытая позиция/);
  assert.match(result.summary, /TP\/SL/);
}

{
  // Legacy records did not persist ShortScore/bearish gap; recover both from contributions.
  const result = analyze(decision({
    hasBearishStructure: true,
    contributions: [
      { name: "EMA", value: -0.25, reason: "fast EMA is below slow EMA by 0.22%" },
      { name: "ShortScore", value: 0, reason: "short diagnostic score 0.7; requires bearish EMA plus downside confirmation" }
    ]
  }));
  assert.equal(result.side, "SHORT");
  assert.ok(result.facts.some(item => item.label === "SHORT score" && item.value === "0.70"));
  assert.ok(result.facts.some(item => item.label === "EMA вниз" && item.value === "+0.220%"));
}

for (const code of [
  "SHORT_EMA_NOT_CONFIRMED", "SHORT_SCORE_BELOW_SIGNAL_THRESHOLD", "SHORT_DOWNSIDE_CONFIRMATION_MISSING",
  "SHORT_RANGE_UNAVAILABLE", "SHORT_PULLBACK_FROM_24H_HIGH_TOO_SMALL", "SHORT_FALLING_SNAPSHOTS_NOT_CONFIRMED",
  "SHORT_SLOPE_NOT_NEGATIVE", "SHORT_FRESH_TAPE_NOT_CONFIRMED", "SHORT_ENTRY_TOO_CLOSE_TO_LOCAL_LOW", "SHORT_ENTRY_DRIFT_TOO_HIGH",
  "LONG_24H_RANGE_UNAVAILABLE", "LONG_24H_RANGE_TOO_NARROW", "LONG_24H_RANGE_POSITION_TOO_HIGH", "LONG_REBOUND_FROM_24H_LOW_TOO_SMALL",
  "LONG_EMA_NOT_CONFIRMED", "LONG_RISING_SNAPSHOTS_NOT_CONFIRMED", "LONG_SHORT_SLOPE_NOT_POSITIVE", "LONG_FRESH_TAPE_NOT_CONFIRMED",
  "LONG_ENTRY_TOO_CLOSE_TO_LOCAL_HIGH", "LONG_ENTRY_DRIFT_TOO_HIGH", "ENTRY_STALE_NEAR_HIGH",
  "LONG_UPPER_RANGE_FRESH_TAPE_NOT_ENOUGH", "LONG_LOW_RANGE_STRONG_CONFIRMATION_MISSING",
  "REJECT_ENTRY_STALE_NEAR_HIGH", "REJECT_FUTURES_RISK",
  "ENTRY_BLACKOUT", "ENTRY_INVALID_SPREAD", "ENTRY_INVALID_REFERENCE_PRICE", "ENTRY_ATR_MISSING", "ENTRY_ATR_STALE",
  "ENTRY_COST_INVALID", "ENTRY_EXITS_INVALID", "ENTRY_VOLUME", "ENTRY_DEPTH", "ENTRY_DEPTH_MISSING", "ENTRY_EXIT_DEPTH",
  "ENTRY_OPEN_RISK_CAP", "ENTRY_OPEN_RISK_UNSAFE", "MAX_POSITIONS", "CYCLE_POSITION_LIMIT",
  "EXPLORATORY_RANK", "EARLY_ENTRY_RANK", "MARKET_REGIME",
  "LIVE_FALLBACK_REJECTED_RISK", "LIVE_FALLBACK_REJECTED_SLIPPAGE", "LIVE_FALLBACK_REJECTED_SPREAD",
  "LIVE_FALLBACK_REJECTED_STALE_QUOTE",
  "REJECT_SCORE_BELOW_THRESHOLD", "REJECT_SPREAD_TOO_WIDE", "REJECT_NO_VOLUME_CONFIRMATION", "REJECT_NO_MOMENTUM_CONFIRMATION",
  "REJECT_NO_DIRECTIONAL_SCORE", "REJECT_ALREADY_HOLDING",
  "REJECT_COOLDOWN", "REJECT_MAX_POSITIONS", "REJECT_DAILY_RISK", "REJECT_RISK_LIMITS", "REJECT_CORRELATION_LIMIT",
  "DESIRED_LONG", "MIN_HOLD_BLOCK", "MIN_PROFIT_BLOCK", "STOPLOSS_COOLDOWN", "HOURLY_ENTRY_LIMIT",
  "LIVE_BROKER_UNAVAILABLE", "LIVE_ORDER_REJECTED", "LIVE_ENTRY_PRICE_DEVIATION", "FILL_RECONCILIATION_PENDING",
  "SELL_STOP_LOSS", "SELL_TAKE_PROFIT", "SELL_MAX_HOLD", "SELL_SIGNAL_FLIP", "SELL_TRAILING_STOP"
]) {
  assert.ok(translations[code], `missing human translation for ${code}`);
}

{
  const page = fs.readFileSync(path.join(__dirname, "../public/cycle-decisions.html"), "utf8");
  assert.match(page, /const cycles = await loadCycles\(1\)/);
  assert.doesNotMatch(page, /loadCycles\(80\)/);
  assert.doesNotMatch(page, /latestMeta=true/);
  assert.match(page, /\/api\/decisions\?limit=/);
  assert.ok(
    page.indexOf("renderDecisionRows(latest)") < page.indexOf("const errorFeed = await errorsPromise"),
    "latest cards must render before the secondary error feed finishes"
  );
}

// A raw low-level hold code must not win over the translated rejection mapped from it.
{
  const result = analyze(decision({
    dryRunAction: { action: "NO_ORDER", holdReasonCode: "ENTRY_VOLUME", entryRejectionReason: "REJECT_LOW_LIQUIDITY" }
  }));
  assert.ok(translations[result.code], `explained code expected, got ${result.code}`);
  assert.doesNotMatch(result.headline, /_/, "headline must not expose a raw code token");
}

// Every guard code reaching the page is explained, and both new guard codes resolve.
for (const code of ["LONG_LOW_RANGE_STRONG_CONFIRMATION_MISSING", "LONG_UPPER_RANGE_FRESH_TAPE_NOT_ENOUGH"]) {
  const result = analyze(decision({ dryRunAction: { action: "NO_ORDER", longRangeBlockReasonCode: code } }));
  assert.equal(result.code, code);
  assert.equal(result.headline, translations[code]);
  assert.doesNotMatch(result.headline, /_/);
}

// An unknown/future code still renders readable text instead of a bare token.
{
  const result = analyze(decision({
    dryRunAction: { action: "NO_ORDER", holdReasonCode: "LONG_SOME_FUTURE_RULE" }
  }));
  assert.equal(result.headline, "Long some future rule");
  assert.match(result.summary, /Long some future rule/);
}

// Entry channel is surfaced in human form.
{
  const result = analyze(decision({
    dryRunAction: { action: "WOULD_OPEN_LONG", side: "LONG", entryChannel: "DipBounce" }
  }));
  const channel = result.facts.find(item => item.label === "Канал входа");
  assert.ok(channel, "entry channel fact expected");
  assert.equal(channel.value, "Отскок от низа диапазона");
}

console.log("decision-explainer tests passed");
