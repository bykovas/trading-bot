(function () {
  function dataLabel(mode) {
    const value = String(mode || "").toLowerCase();
    if (value === "kraken") return "Kraken data";
    if (value === "kraken-futures") return "Kraken Futures data";
    if (value === "sample") return "Sample data";
    return "Data unknown";
  }

  function runtimeLabel(status) {
    if (!status || status.runtimeState === "no-data") return "No cycles";
    if (status.runtimeState === "stale") return "Stale";
    if (status.runtimeState === "night-window") return "Night window";
    return "Running";
  }

  function statusKind(status) {
    if (!status || status.runtimeState === "no-data" || status.runtimeState === "stale") return "bad";
    if (status.runtimeState === "night-window") return "warn";
    return "ok";
  }

  function ageLabel(seconds) {
    const n = Number(seconds);
    if (!Number.isFinite(n)) return "";
    if (n < 60) return `updated ${Math.max(0, Math.round(n))}s ago`;
    return `updated ${Math.round(n / 60)}m ago`;
  }

  function fullLabel(status) {
    const parts = [dataLabel(status?.marketDataMode), runtimeLabel(status)];
    const age = ageLabel(status?.latestCycleAgeSeconds);
    if (age) parts.push(age);
    return parts.join(" · ");
  }

  function blackoutTitle(status) {
    const blackout = status?.entryBlackout;
    if (!blackout?.configured) return "";
    const window = blackout.localStart && blackout.localEnd
      ? `Entry blackout ${blackout.localStart}-${blackout.localEnd} ${blackout.localTimeZone || "LT"}`
      : "Entry blackout configured";
    return blackout.isActive ? `${window}; new entries paused` : window;
  }

  async function fetchStatus(botInstanceId) {
    const suffix = botInstanceId ? `?botInstanceId=${encodeURIComponent(botInstanceId)}` : "";
    const response = await fetch(`/api/bot-status${suffix}`, { cache: "no-store" });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    return response.json();
  }

  async function updateBadge(options) {
    const textEl = document.getElementById(options.textId || "status-text");
    const dotEl = document.getElementById(options.dotId || "status-dot");
    if (!textEl) return null;

    const status = await fetchStatus(options.botInstanceId);
    textEl.textContent = fullLabel(status);
    textEl.title = blackoutTitle(status);
    if (dotEl) {
      const kind = statusKind(status);
      dotEl.className = `dot ${kind === "ok" ? "ok" : kind === "bad" ? "bad" : "warn"}`;
    }
    return status;
  }

  window.BlynaiBotStatus = {
    updateBadge,
    fetchStatus,
    dataLabel,
    runtimeLabel,
    fullLabel
  };
})();
