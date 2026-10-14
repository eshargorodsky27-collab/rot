#!/usr/bin/env bash
# rot always-on watcher. Runs under pm2: every ~60s it checks #rot-trades for new R0T posts, writes
# the same board JSON the GitHub Action did (new/*.json + all-messages.jsonl + .last-<channel>), then
# commits and pushes. Replaces the throttled GitHub cron with ~1-minute capture.
#
# The Discord token is read from ~/rot/.env (DISCORD_TOKEN=...) on every cycle and never leaves this
# machine. .env is gitignored. Paper/data only — nothing here bets or trades.
set -uo pipefail
cd "$(dirname "$0")" || exit 1
export PATH="$HOME/.dotnet:$PATH"
BIN="./.dce-bin/DiscordChatExporter.Cli"
CHANNEL="1519487140231123144"   # RICH0FFTENN1S #rot-trades
AUTHOR="309854776307351554"     # only R0T's posts
LOG="./.sync.log"
INTERVAL=60

log() { echo "[$(date '+%Y-%m-%d %H:%M:%S')] $*" >> "$LOG"; }
log "watcher started (pid $$)"

while true; do
  # reload token each cycle so dropping it into .env takes effect with no restart
  DISCORD_TOKEN=""
  if [ -f ./.env ]; then set -a; . ./.env; set +a; fi
  if [ -z "${DISCORD_TOKEN:-}" ]; then
    log "waiting for DISCORD_TOKEN in ~/rot/.env"; sleep 30; continue
  fi
  # strip stray quotes/spaces/newlines that sneak in on paste
  DISCORD_TOKEN="$(printf '%s' "$DISCORD_TOKEN" | tr -d '"'"'"' \r\n\t')"
  export DISCORD_TOKEN

  "$BIN" watch --once --channel "$CHANNEL" --author "$AUTHOR" --minimal --output . >> "$LOG" 2>&1 \
    || log "exporter exited non-zero (see above); will retry"

  git add -A new all-messages.jsonl .last-* 2>/dev/null || true
  if ! git diff --cached --quiet; then
    n=$(git diff --cached --name-only -- new | wc -l | tr -d ' ')
    git -c user.name="rot-watch" -c user.email="rot-watch@local" commit -q -m "Add ${n} new rot-trades post(s)"
    git pull --rebase -q origin main >> "$LOG" 2>&1 || log "rebase hiccup"
    if git push -q origin main >> "$LOG" 2>&1; then
      log "pushed ${n} new post(s)"
    else
      log "push failed; will retry next cycle"
    fi
  fi

  sleep "$INTERVAL"
done
