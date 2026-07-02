# Blynai Capital Design System - v0.1

## Product Tone

Blynai Capital is a fake-but-premium micro hedge fund: serious engineering, unserious fund energy. The interface should feel like a high-end trading cockpit that happens to be run by a self-hosted pancake fund, not like a generic crypto SaaS dashboard.

The brand must be visible in the first viewport. Do not hide "Blynai Capital" as a tiny logo. The brand is part of the product's memorability.

## Visual Direction

- Dark cinematic fintech base, but not blue-only and not casino-neon.
- Warm fund identity: cream, butter gold, amber, charred black, restrained green.
- Pancake references are abstract: circular coin marks, stacked-line details, radar-ring backgrounds, fund seals.
- Never use cartoon pancakes, syrup drips, mascot art, or childish food illustration.
- Data remains serious: tabular numbers, clear state colors, replayable traces, low-copy panels.

## Color Tokens

| Token | Hex | Use |
|---|---:|---|
| `bg-void` | `#050505` | page base |
| `bg-deep` | `#090908` | terminal depth |
| `surface` | `#11120f` | main panels |
| `surface-strong` | `#181914` | raised terminal surfaces |
| `ink` | `#f6efe1` | primary text |
| `ink-soft` | `#c8bba6` | secondary text |
| `muted` | `#82796d` | metadata |
| `cream` | `#f7e6c1` | premium highlight |
| `gold` | `#f5b84f` | brand warmth |
| `green` | `#41eda0` | profit, long, approved, OK |
| `red` | `#ff5367` | loss, rejected, error, kill switch |
| `amber` | `#f0a63a` | warning, cooldown, risk attention |
| `blue` | `#6faeff` | info, hold, neutral |

## Typography

- UI font: system sans (`Inter`, `ui-sans-serif`, `Segoe UI`, sans-serif fallback).
- Numeric and trace font: system monospace (`SFMono-Regular`, `Cascadia Mono`, `Roboto Mono`, `Consolas`).
- Numbers must use monospace styling when they represent price, quantity, score, timestamps, order IDs, fees, and hashes.
- Keep letter spacing at `0`. Use weight, color, and spacing for hierarchy.

## Layout

- Desktop-first cockpit around 1440px wide.
- First screen should be one impressive Decision Explanation view, not a full dashboard.
- Recommended structure:
  - global status strip
  - prominent Blynai Capital hero brand band
  - large chart panel as central visual story
  - score waterfall and final verdict
  - risk timeline
  - execution trace
  - compact audit snapshot
- Panel radius: `8px`.
- Avoid cards inside cards. Use sections, dividers, and terminal panels.

## Component Semantics

- `Status pill`: tiny live-state indicator. Green means OK/live, muted means disabled.
- `Kill switch`: red, always visible, never visually subtle.
- `Brand mark`: abstract pancake coin or fund seal. Should feel premium and slightly absurd.
- `Chart panel`: must include current price, EMA9/EMA21, regime band, buy marker, and replay metadata.
- `Score waterfall`: weighted signal contributions with positive/negative/info coloring.
- `Risk timeline`: pass/fail sequence. The risk manager is a first-class actor, not a footnote.
- `Execution trace`: grid of final order facts, with monospace values.
- `Audit snapshot`: compact provenance for replayability.

## Current Mockup

The first implemented static screen lives at:

`ui-mockups/blynai-decision/index.html`

It represents one BUY decision for `SOL/EUR` on Kraken Pro using `EMA-Cross v1.2`.
