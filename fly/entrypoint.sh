#!/usr/bin/env bash
# rot fly worker: clone the repo, then every ~60s capture R0T's new posts and commit + push.
# Secrets come from fly (DISCORD_TOKEN, GIT_TOKEN); neither is baked into the image or any git URL.
set -uo pipefail
: "${DISCORD_TOKEN:?missing DISCORD_TOKEN fly secret}"
: "${GIT_TOKEN:?missing GIT_TOKEN fly secret}"
REPO_SLUG="eshargorodsky27-collab/rot"
CHANNEL="1519487140231123144"   # RICH0FFTENN1S #rot-trades
AUTHOR="309854776307351554"     # only R0T's posts
INTERVAL="${INTERVAL:-60}"
WORK=/work
BIN=/app/dce/DiscordChatExporter.Cli
export GIT_ASKPASS=/app/askpass.sh   # feeds the token to git push; keeps it out of any URL/reflog
ts(){ date '+%Y-%m-%d %H:%M:%S'; }

echo "[$(ts)] rot-fly starting; cloning ${REPO_SLUG}..."
rm -rf "$WORK"
git clone --depth 100 "https://github.com/${REPO_SLUG}.git" "$WORK" || { echo "[$(ts)] clone failed"; exit 1; }
cd "$WORK"
git config user.name "rot-fly"
git config user.email "rot-fly@users.noreply.github.com"

TOK="$(printf '%s' "$DISCORD_TOKEN" | tr -d '"'"'"' \r\n\t')"
echo "[$(ts)] watching #rot-trades every ${INTERVAL}s -> pushing to ${REPO_SLUG}"
while true; do
  DISCORD_TOKEN="$TOK" "$BIN" watch --once --channel "$CHANNEL" --author "$AUTHOR" --minimal --output . 2>&1 | tail -3
  git add -A new all-messages.jsonl .last-* 2>/dev/null || true
  if ! git diff --cached --quiet; then
    n=$(git diff --cached --name-only -- new | wc -l | tr -d ' ')
    git commit -q -m "Add ${n} new rot-trades post(s)"
    git pull --rebase -q origin main || echo "[$(ts)] rebase hiccup"
    if git push -q origin main; then echo "[$(ts)] pushed ${n} post(s)"; else echo "[$(ts)] push failed; retry next cycle"; fi
  fi
  sleep "$INTERVAL"
done
