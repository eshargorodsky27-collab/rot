# rot

Auto-synced trade posts from **#rot-trades** (RICH0FFTENN1S), for the VixyVault board.

A GitHub Action (`.github/workflows/rot-sync.yml`) checks the channel every 5 minutes (paused 12am-6am ET)
and commits anything new. Only R0T's posts are saved, and only what the board needs.

- `new/` - one JSON file per post (`author`, `content`, `timestamp`, `jumpUrl`, `attachments`, `embeds`)
- `all-messages.jsonl` - every post, one per line, oldest first

The exporter in `tools/dce/` is [DiscordChatExporter](https://github.com/Tyrrrz/DiscordChatExporter) (MIT) with an added `watch` command.
