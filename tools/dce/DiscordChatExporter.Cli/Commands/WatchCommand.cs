using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CliFx.Binding;
using CliFx.Infrastructure;
using DiscordChatExporter.Cli.Commands.Base;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exceptions;

namespace DiscordChatExporter.Cli.Commands;

[Command(
    "watch",
    Description = "Watches a channel and saves every new message as JSON as soon as it is seen."
)]
public partial class WatchCommand : DiscordCommandBase
{
    [CommandOption("channel", 'c', Description = "Channel ID. If omitted, --guild-name and --channel-name are used to find it.")]
    public Snowflake? ChannelId { get; set; }

    [CommandOption("guild-name", Description = "Server name (or part of it) to search for.")]
    public string? GuildName { get; set; }

    [CommandOption("channel-name", Description = "Channel name (or part of it) to search for.")]
    public string? ChannelName { get; set; }

    [CommandOption("interval", 'i', Description = "Seconds between checks (minimum 15). A few seconds of random jitter is added.")]
    public int IntervalSeconds { get; set; } = 60;

    [CommandOption("output", 'o', Description = "Folder to write new messages into.")]
    public string OutputDir { get; set; } = "watch-output";

    [CommandOption("webhook", Description = "Optional URL; every new message is POSTed to it as JSON.")]
    public string? WebhookUrl { get; set; }

    [CommandOption("backfill", Description = "On the very first run, also grab this many recent messages.")]
    public int Backfill { get; set; } = 0;

    [CommandOption("quiet-from", Description = "Hour (0-23, local time) to pause checking. Use with --quiet-until.")]
    public int? QuietFromHour { get; set; }

    [CommandOption("quiet-until", Description = "Hour (0-23, local time) to resume checking.")]
    public int? QuietUntilHour { get; set; }

    [CommandOption("author", Description = "Only save messages from this user ID; everyone else's messages are skipped.")]
    public Snowflake? OnlyAuthorId { get; set; }

    [CommandOption("minimal", Description = "Save only what a public board needs: text, images, embeds, time and link. No user IDs, avatars or mentions.")]
    public bool Minimal { get; set; } = false;

    [CommandOption("once", Description = "Check once, save anything new, then exit (for schedulers like GitHub Actions).")]
    public bool Once { get; set; } = false;

    private TimeSpan? GetQuietWait()
    {
        if (QuietFromHour is not { } from || QuietUntilHour is not { } until || from == until)
            return null;

        var now = DateTime.Now;
        var inQuiet = from < until
            ? now.Hour >= from && now.Hour < until
            : now.Hour >= from || now.Hour < until;
        if (!inQuiet)
            return null;

        var resume = now.Date.AddHours(until);
        if (resume <= now)
            resume = resume.AddDays(1);
        return resume - now;
    }

    private static string Normalize(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private async Task<Channel> ResolveChannelAsync(IConsole console, CancellationToken ct)
    {
        if (ChannelId is { } id)
            return await Discord.GetChannelAsync(id, ct);

        if (string.IsNullOrWhiteSpace(GuildName) || string.IsNullOrWhiteSpace(ChannelName))
            throw new DiscordChatExporterException(
                "Provide either --channel <id>, or both --guild-name and --channel-name."
            );

        var guilds = new List<Guild>();
        await foreach (var g in Discord.GetUserGuildsAsync(ct))
            guilds.Add(g);

        var wantGuild = Normalize(GuildName);
        var guild =
            guilds.FirstOrDefault(g => Normalize(g.Name) == wantGuild)
            ?? guilds.FirstOrDefault(g => Normalize(g.Name).Contains(wantGuild))
            ?? throw new DiscordChatExporterException(
                $"No server matching '{GuildName}'. Servers you're in: "
                    + string.Join(", ", guilds.Select(g => g.Name))
            );

        var channels = new List<Channel>();
        await foreach (var c in Discord.GetGuildChannelsAsync(guild.Id, ct))
            if (!c.IsCategory && !c.IsVoice)
                channels.Add(c);

        var wantChannel = Normalize(ChannelName);
        var channel =
            channels.FirstOrDefault(c => Normalize(c.Name) == wantChannel)
            ?? channels.FirstOrDefault(c => Normalize(c.Name).Contains(wantChannel))
            ?? throw new DiscordChatExporterException(
                $"No channel matching '{ChannelName}' in '{guild.Name}'. Channels: "
                    + string.Join(", ", channels.Select(c => c.Name))
            );

        await console.Output.WriteLineAsync(
            $"Found #{channel.Name} (id {channel.Id}) in {guild.Name} (id {guild.Id})."
        );
        return channel;
    }

    private static byte[] ToJson(Message m, Channel channel, bool minimal)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("id", m.Id.ToString());
            w.WriteString("channelName", channel.Name);
            w.WriteString(
                "jumpUrl",
                $"https://discord.com/channels/{channel.GuildId}/{channel.Id}/{m.Id}"
            );
            w.WriteString("timestamp", m.Timestamp.ToUniversalTime());
            if (m.EditedTimestamp is { } edited)
                w.WriteString("editedTimestamp", edited.ToUniversalTime());
            w.WriteString("type", m.Kind.ToString());

            if (minimal)
            {
                w.WriteString("author", m.Author.DisplayName);
                // Replace user mentions like <@123> so no one else's account ID is published
                w.WriteString(
                    "content",
                    System.Text.RegularExpressions.Regex.Replace(m.Content, @"<@!?\d+>", "@user")
                );
            }
            else
            {
                w.WriteStartObject("author");
                w.WriteString("id", m.Author.Id.ToString());
                w.WriteString("username", m.Author.Name);
                w.WriteString("displayName", m.Author.DisplayName);
                w.WriteBoolean("isBot", m.Author.IsBot);
                w.WriteString("avatarUrl", m.Author.AvatarUrl);
                w.WriteEndObject();
                w.WriteString("content", m.Content);
            }

            if (!minimal && m.Reference?.MessageId is { } replyTo)
                w.WriteString("replyToMessageId", replyTo.ToString());

            w.WriteStartArray("attachments");
            foreach (var a in m.Attachments)
            {
                w.WriteStartObject();
                w.WriteString("fileName", a.FileName);
                w.WriteString("url", a.Url);
                w.WriteBoolean("isImage", a.IsImage);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("embeds");
            foreach (var e in m.Embeds)
            {
                w.WriteStartObject();
                if (e.Title is not null) w.WriteString("title", e.Title);
                if (e.Description is not null) w.WriteString("description", e.Description);
                if (e.Url is not null) w.WriteString("url", e.Url);
                if (e.Image?.Url is not null) w.WriteString("imageUrl", e.Image.Url);
                w.WriteStartArray("fields");
                foreach (var f in e.Fields)
                {
                    w.WriteStartObject();
                    w.WriteString("name", f.Name);
                    w.WriteString("value", f.Value);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();

            if (!minimal)
            {
                w.WriteStartArray("mentions");
                foreach (var u in m.MentionedUsers)
                    w.WriteStringValue(u.Name);
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }
        return buffer.ToArray();
    }

    private static Snowflake? ReadState(string path) =>
        File.Exists(path) ? Snowflake.TryParse(File.ReadAllText(path).Trim()) : null;

    private static void WriteState(string path, Snowflake id)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, id.ToString());
        File.Move(tmp, path, true);
    }

    public override async ValueTask ExecuteAsync(IConsole console)
    {
        await base.ExecuteAsync(console);
        var ct = console.RegisterCancellationHandler();

        var interval = TimeSpan.FromSeconds(Math.Max(15, IntervalSeconds));
        var channel = await ResolveChannelAsync(console, ct);

        var outDir = Path.GetFullPath(OutputDir);
        var inbox = Path.Combine(outDir, "new");
        Directory.CreateDirectory(inbox);
        var statePath = Path.Combine(outDir, $".last-{channel.Id}");
        var logPath = Path.Combine(outDir, "all-messages.jsonl");

        var lastId = ReadState(statePath);
        if (lastId is null)
        {
            if (Backfill > 0)
            {
                // Start just before the N most recent messages
                var recent = new List<Message>();
                await foreach (var m in Discord.GetMessagesInReverseAsync(channel.Id, null, null, null, ct))
                {
                    recent.Add(m);
                    if (recent.Count >= Backfill) break;
                }
                lastId = recent.Count > 0
                    ? new Snowflake(recent.Last().Id.Value - 1)
                    : channel.LastMessageId ?? Snowflake.FromDate(DateTimeOffset.UtcNow);
            }
            else
            {
                // Only messages from now on
                lastId = channel.LastMessageId ?? Snowflake.FromDate(DateTimeOffset.UtcNow);
            }
            WriteState(statePath, lastId.Value);
        }

        await console.Output.WriteLineAsync(
            $"Watching #{channel.Name} every ~{interval.TotalSeconds:0}s. New messages -> {inbox}"
        );

        var random = new Random();
        var failures = 0;

        while (!ct.IsCancellationRequested)
        {
            if (GetQuietWait() is { } quietWait)
            {
                if (Once)
                {
                    await console.Output.WriteLineAsync("Quiet hours - nothing to do.");
                    break;
                }

                await console.Output.WriteLineAsync(
                    $"[{DateTimeOffset.Now:HH:mm:ss}] Quiet hours - pausing until {DateTime.Now + quietWait:HH:mm}."
                );
                try { await Task.Delay(quietWait + TimeSpan.FromSeconds(random.Next(0, 120)), ct); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                var count = 0;
                await foreach (var m in Discord.GetMessagesAsync(channel.Id, lastId, null, null, ct))
                {
                    if (OnlyAuthorId is { } onlyAuthor && m.Author.Id != onlyAuthor)
                    {
                        // Not from the allowed author: skip it, but remember we've seen it
                        lastId = m.Id;
                        WriteState(statePath, m.Id);
                        continue;
                    }

                    var json = ToJson(m, channel, Minimal);

                    // One file per message (easy for other code to pick up), plus a running log
                    var file = Path.Combine(inbox, $"{m.Timestamp.ToUniversalTime():yyyyMMdd-HHmmss}-{m.Id}.json");
                    await File.WriteAllBytesAsync(file + ".tmp", json, ct);
                    File.Move(file + ".tmp", file, true);

                    using var doc = JsonDocument.Parse(json);
                    var compact = JsonSerializer.Serialize(doc.RootElement, JsonContext.Default.JsonElement);
                    await File.AppendAllTextAsync(logPath, compact + "\n", ct);

                    if (!string.IsNullOrWhiteSpace(WebhookUrl))
                    {
                        using var content = new ByteArrayContent(json);
                        content.Headers.ContentType = new("application/json");
                        using var resp = await Core.Utils.Http.Client.PostAsync(WebhookUrl, content, ct);
                        if (!resp.IsSuccessStatusCode)
                            await console.Error.WriteLineAsync($"Webhook returned {(int)resp.StatusCode} for message {m.Id}");
                    }

                    lastId = m.Id;
                    WriteState(statePath, m.Id);
                    count++;

                    var preview = m.Content.Length > 80 ? m.Content[..80] + "…" : m.Content;
                    await console.Output.WriteLineAsync(
                        $"[{DateTimeOffset.Now:HH:mm:ss}] {m.Author.DisplayName}: {preview.Replace('\n', ' ')}"
                    );
                }
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (DiscordChatExporterException ex) when (ex.IsFatal)
            {
                await console.Error.WriteLineAsync($"Stopping: {ex.Message}");
                throw;
            }
            catch (Exception ex) when (!Once)
            {
                failures++;
                await console.Error.WriteLineAsync(
                    $"[{DateTimeOffset.Now:HH:mm:ss}] Check failed ({ex.Message}); backing off."
                );
            }

            if (Once)
                break;

            // Back off on repeated failures (up to 15 min), plus random jitter
            var wait = interval * Math.Min(Math.Pow(2, failures), 15)
                + TimeSpan.FromSeconds(random.Next(0, 10));
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { break; }
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonElement))]
internal partial class JsonContext : System.Text.Json.Serialization.JsonSerializerContext;
