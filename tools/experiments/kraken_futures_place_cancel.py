#!/usr/bin/env python3
"""
Manual one-shot experiment: place ONE passive post-only buy on Kraken Futures far below the market, then cancel it.
Checks auth, symbol, size step, tick, cliOrdId, response parsing and cancel — without a fill under normal conditions.

Run by the owner only. Default is a dry run that prints the plan and touches nothing private.
Real order only with --live AND typing the confirmation word.

Keys come from the environment of the shell that runs it (never printed):
  KRAKEN_FUTURES_KEY, KRAKEN_FUTURES_SECRET   — a key of a SEPARATE subaccount, trade permission only, no withdraw
Optional Telegram alerts:
  TELEGRAM_BOT_TOKEN, TELEGRAM_CHAT_ID

Safety rules built in:
  * notional (size x limit price) must be <= MAX_EUR; never increases size
  * refuses if the account has ANY open order or position
  * post-only order 5 % below the bid; processBefore = now + 10 s
  * dead-man's switch: cancelallordersafter 60 s before sending
  * one send, no automatic retry; on timeout/5xx it queries status by cliOrdId first
  * cancels by cliOrdId; if anything filled, closes exactly the filled size with reduceOnly
  * verifies no open order and no position at the end
"""
import argparse, base64, hashlib, hmac, json, os, sys, time, urllib.parse, urllib.request, uuid
from decimal import Decimal, ROUND_DOWN

BASE = "https://futures.kraken.com"
SYMBOL = "PF_ADAUSD"
MAX_EUR = Decimal("1")
OFFSET = Decimal("0.05")  # 5 % below bid


def pub(path):
    with urllib.request.urlopen(BASE + path, timeout=10) as r:
        return json.load(r)


def eurusd():
    with urllib.request.urlopen("https://api.kraken.com/0/public/Ticker?pair=EURUSD", timeout=10) as r:
        d = json.load(r)["result"]
    return Decimal(next(iter(d.values()))["c"][0])


def signed(method, endpoint, params=None, timeout=10):
    key, secret = os.environ["KRAKEN_FUTURES_KEY"], os.environ["KRAKEN_FUTURES_SECRET"]
    post = urllib.parse.urlencode(params or {})
    nonce = str(int(time.time() * 1000))
    path = endpoint.replace("/derivatives", "")  # signature covers /api/v3/...
    digest = hashlib.sha256((post + nonce + path).encode()).digest()
    sig = base64.b64encode(hmac.new(base64.b64decode(secret), digest, hashlib.sha512).digest()).decode()
    url = BASE + endpoint + (("?" + post) if method == "GET" and post else "")
    data = post.encode() if method == "POST" else None
    req = urllib.request.Request(url, data=data, method=method,
                                 headers={"APIKey": key, "Nonce": nonce, "Authent": sig,
                                          "Content-Type": "application/x-www-form-urlencoded"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.load(r)


def tg(text):
    tok, chat = os.environ.get("TELEGRAM_BOT_TOKEN"), os.environ.get("TELEGRAM_CHAT_ID")
    print("ALERT:", text)
    if tok and chat:
        try:
            urllib.request.urlopen(f"https://api.telegram.org/bot{tok}/sendMessage",
                                   data=urllib.parse.urlencode({"chat_id": chat, "text": "EXPERIMENT · " + text}).encode(), timeout=10)
        except Exception as e:  # never let the alert hide the real problem
            print("telegram failed:", type(e).__name__)


def state():
    oo = signed("GET", "/derivatives/api/v3/openorders").get("openOrders", [])
    op = signed("GET", "/derivatives/api/v3/openpositions").get("openPositions", [])
    return oo, op


def status(cli):
    return signed("POST", "/derivatives/api/v3/orders/status", {"cliOrdIds": cli})


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--live", action="store_true", help="really send (asks for confirmation)")
    ap.add_argument("--shared-account", action="store_true",
                    help="account used by a live bot (e.g. BYKO): no dead-man switch (it would cancel the bot's TP/SL), "
                         "existing orders/positions allowed but none on the experiment symbol; only our cliOrdId is touched")
    ap.add_argument("--env-file", help="read KRAKEN_FUTURES_KEY/SECRET from this env file (values never printed)")
    ap.add_argument("--key-var", default="KRAKEN_FUTURES_KEY")
    ap.add_argument("--secret-var", default="KRAKEN_FUTURES_SECRET")
    ap.add_argument("--telegram-token-var", default="TRADINGBOT_TELEGRAM_BOT_TOKEN", help="token variable in --env-file")
    ap.add_argument("--telegram-chat", help="chat id for alerts (e.g. the bots' group)")
    a = ap.parse_args()

    inst = next(i for i in pub("/derivatives/api/v3/instruments")["instruments"] if i.get("symbol") == SYMBOL)
    tick = Decimal(str(inst["tickSize"]))
    prec = int(inst.get("contractValueTradePrecision", 0))
    step = Decimal(1).scaleb(-prec)
    size = step  # one step; never more
    t = next(x for x in pub("/derivatives/api/v3/tickers")["tickers"] if x.get("symbol") == SYMBOL)
    bid = Decimal(str(t["bid"]))
    limit = (bid * (1 - OFFSET) / tick).to_integral_value(ROUND_DOWN) * tick
    notional_eur = size * limit / eurusd()
    cli = f"exp-{time.strftime('%Y%m%dT%H%M%SZ', time.gmtime())}-{uuid.uuid4().hex[:8]}"
    print(f"plan: {SYMBOL} post buy size={size} limit={limit} (bid {bid}, tick {tick}, step {step}) "
          f"notional≈{notional_eur:.4f} EUR cliOrdId={cli}")
    if notional_eur > MAX_EUR:
        print("STOP: one-step notional exceeds the limit; size is never increased."); return 2
    if not a.live:
        print("dry run — nothing private touched. Re-run with --live to send."); return 0
    if a.env_file:
        for line in open(a.env_file):
            k, _, v = line.strip().partition("=")
            if k == a.key_var: os.environ["KRAKEN_FUTURES_KEY"] = v.strip().strip('"')
            if k == a.secret_var: os.environ["KRAKEN_FUTURES_SECRET"] = v.strip().strip('"')
            if k == a.telegram_token_var and v.strip(): os.environ["TELEGRAM_BOT_TOKEN"] = v.strip().strip('"')
    if a.telegram_chat:
        os.environ["TELEGRAM_CHAT_ID"] = a.telegram_chat
    for k in ("KRAKEN_FUTURES_KEY", "KRAKEN_FUTURES_SECRET"):
        if not os.environ.get(k):
            print(f"missing {k} in the environment"); return 2

    oo, op = state()
    mine = lambda xs: [x for x in xs if x.get("symbol", "").upper() == SYMBOL]
    if a.shared_account:
        if mine(oo) or mine(op):
            print(f"STOP: the account already has an order or position on {SYMBOL}; choose another symbol."); return 2
        print(f"shared account: {len(oo)} existing orders and {len(op)} positions on other symbols are left untouched")
    elif oo or op:
        print(f"STOP: account has {len(oo)} open orders and {len(op)} positions — use an empty subaccount or --shared-account."); return 2
    if input("type SEND to place the order: ").strip() != "SEND":
        print("not confirmed"); return 0

    if not a.shared_account:
        signed("POST", "/derivatives/api/v3/cancelallordersafter", {"timeout": 60})
    sent = None
    try:
        sent = signed("POST", "/derivatives/api/v3/sendorder", {
            "orderType": "post", "symbol": SYMBOL, "side": "buy", "size": str(size), "limitPrice": str(limit),
            "cliOrdId": cli, "processBefore": time.strftime("%Y-%m-%dT%H:%M:%S.000Z", time.gmtime(time.time() + 10))})
        print("sendorder:", json.dumps(sent.get("sendStatus", sent))[:500])
    except Exception as e:
        tg(f"sendorder {type(e).__name__}; checking status by cliOrdId, no retry")
    time.sleep(1)
    try:
        print("status:", json.dumps(status(cli))[:500])
    except Exception as e:
        tg(f"status query failed: {type(e).__name__}")

    try:
        c = signed("POST", "/derivatives/api/v3/cancelorder", {"cliOrdId": cli})
        print("cancel:", json.dumps(c.get("cancelStatus", c))[:300])
    except Exception as e:
        tg(f"cancel failed: {type(e).__name__} — check the account by hand")

    fills = [f for f in signed("GET", "/derivatives/api/v3/fills").get("fills", []) if f.get("cliOrdId") == cli]
    filled = sum(Decimal(str(f.get("size", 0))) for f in fills)
    if filled > 0:
        tg(f"filled {filled} {SYMBOL}; closing exactly that with reduceOnly")
        try:
            signed("POST", "/derivatives/api/v3/sendorder", {"orderType": "mkt", "symbol": SYMBOL, "side": "sell",
                                                               "size": str(filled), "reduceOnly": "true",
                                                               "cliOrdId": cli + "-close"})
        except Exception as e:
            tg(f"reduceOnly close failed: {type(e).__name__} — close by hand")

    if not a.shared_account:
        signed("POST", "/derivatives/api/v3/cancelallordersafter", {"timeout": 0})  # switch the dead-man timer off
    oo, op = state()
    if a.shared_account:
        oo, op = mine(oo), mine(op)
    print(f"final on {'%s' % SYMBOL if a.shared_account else 'the account'}: open orders {len(oo)}, positions {len(op)}")
    if oo or op:
        tg(f"residual: {len(oo)} open orders, {len(op)} positions on the experiment account"); return 1
    print("OK: order accepted and cancelled (or rejected) — nothing left on the account.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
