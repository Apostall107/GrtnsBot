using System.Globalization;
using System.Security.Claims;
using Discord;
using Discord.WebSocket;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

// Посты от имени бота: предпросмотр разбивки, отправка сразу или по времени, история.
public static partial class PanelApi
{
    private static void MapAnnounce(RouteGroupBuilder g)
    {
        // Как текст разобьётся на сообщения Discord
        g.MapPost("/preview", (AnnounceBody body) =>
        {
            var text = (body.Content ?? "").Trim();
            if (text.Length == 0) return Error("Write the text of the post.");
            if (text.Length > PostSplitter.MaxTotalLength) return Error($"The post is limited to {PostSplitter.MaxTotalLength} characters.");
            var blocks = PostSplitter.Split(text);
            return Results.Ok(new { blocks, count = blocks.Count, length = text.Length });
        });

        g.MapGet("/", (ulong guildId, DiscordSocketClient client, AnnouncementStore store) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var all = store.Get(guildId);
            return Results.Ok(new
            {
                scheduled = all.Where(a => a.Status is "scheduled" or "sending").OrderBy(a => a.SendAt).Select(a => AnnounceDto(guild, a)),
                sent = all.Where(a => a.Status is "sent" or "failed").OrderByDescending(a => a.SentAt ?? a.CreatedAt).Select(a => AnnounceDto(guild, a))
            });
        });

        // Отправить сейчас или запланировать (подтверждение — на сайте, после предпросмотра)
        g.MapPost("/", async (ulong guildId, AnnounceBody body, ClaimsPrincipal user, DiscordSocketClient client, AnnouncementStore store,
            AnnouncementService sender, [FromServices] TimeService time) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var (error, channelId, sendAt) = ValidateAnnounce(guild, body, time);
            if (error is not null) return Error(error);

            var post = new Announcement
            {
                GuildId = guildId,
                ChannelId = channelId,
                Content = body.Content!.Trim(),
                AllowMentions = body.AllowMentions,
                SendAt = sendAt,
                CreatedBy = user.UserId()!.Value
            };
            if (store.Add(post) is { } limit) return Error(limit);
            if (sendAt is not null) return Results.Ok(new { scheduled = true, id = post.Id });

            var result = await sender.SendAsync(post.Id);
            return result is { Status: "sent" }
                ? Results.Ok(new { sent = result.MessageIds.Count, jumpUrl = AnnounceJumpUrl(guildId, result) })
                : Error(result?.Error ?? "The post could not be sent.");
        });

        // Изменить запланированный пост
        g.MapPut("/{id}", (ulong guildId, string id, AnnounceBody body, DiscordSocketClient client, AnnouncementStore store,
            [FromServices] TimeService time) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (store.Find(id) is not { Status: "scheduled" } existing || existing.GuildId != guildId)
                return Error("This post is no longer scheduled.");
            var (error, channelId, sendAt) = ValidateAnnounce(guild, body, time);
            if (error is not null) return Error(error);
            if (sendAt is null) return Error("Pick the date and time (or use Send now).");
            store.Update(id, a =>
            {
                a.ChannelId = channelId;
                a.Content = body.Content!.Trim();
                a.AllowMentions = body.AllowMentions;
                a.SendAt = sendAt;
            });
            return Results.Ok();
        });

        g.MapPost("/{id}/send-now", async (ulong guildId, string id, AnnouncementStore store, AnnouncementService sender) =>
        {
            if (store.Find(id) is not { Status: "scheduled" } existing || existing.GuildId != guildId)
                return Error("This post is no longer scheduled.");
            var result = await sender.SendAsync(id);
            return result is { Status: "sent" }
                ? Results.Ok(new { sent = result.MessageIds.Count, jumpUrl = AnnounceJumpUrl(guildId, result) })
                : Error(result?.Error ?? "The post could not be sent.");
        });

        // Отменить запланированный / убрать из истории (сообщения в Discord остаются)
        g.MapDelete("/{id}", (ulong guildId, string id, AnnouncementStore store) =>
        {
            if (store.Find(id) is not { } existing || existing.GuildId != guildId) return Error("Post not found.");
            if (existing.Status == "sending") return Error("The post is being sent right now.");
            store.Remove(id);
            return Results.Ok();
        });

        // Удалить отправленные сообщения поста в Discord
        g.MapPost("/{id}/delete-messages", async (ulong guildId, string id, AnnouncementStore store, AnnouncementService sender) =>
        {
            if (store.Find(id) is not { } existing || existing.GuildId != guildId) return Error("Post not found.");
            var deleted = await sender.DeleteMessagesAsync(existing);
            store.Remove(id);
            return Results.Ok(new { deleted });
        });
    }

    private static (string? Error, ulong ChannelId, DateTimeOffset? SendAt) ValidateAnnounce(SocketGuild guild, AnnounceBody body, TimeService time)
    {
        var text = (body.Content ?? "").Trim();
        if (text.Length == 0) return ("Write the text of the post.", 0, null);
        if (text.Length > PostSplitter.MaxTotalLength) return ($"The post is limited to {PostSplitter.MaxTotalLength} characters.", 0, null);
        if (ParseId(body.ChannelId) is not { } channelId || guild.GetTextChannel(channelId) is not { } channel)
            return ("Pick a channel.", 0, null);
        if (guild.CurrentUser.GetPermissions(channel) is not { ViewChannel: true, SendMessages: true })
            return ($"I can't post in #{channel.Name}. Give the bot View Channel and Send Messages there.", 0, null);
        if (body.AllowMentions && !guild.CurrentUser.GetPermissions(channel).MentionEveryone && text.Contains("@everyone"))
            return ("The bot can't ping @everyone in this channel (Mention Everyone permission).", 0, null);

        DateTimeOffset? sendAt = null;
        if (!string.IsNullOrWhiteSpace(body.Date) || !string.IsNullOrWhiteSpace(body.Time))
        {
            if (!DateOnly.TryParseExact(body.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                !TimeOnly.TryParseExact(body.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                return ("Pick both the date and the time.", 0, null);
            sendAt = time.ToUtc(date, t.Hour, t.Minute);
            if (sendAt < DateTimeOffset.UtcNow) return ("This time is in the past.", 0, null);
        }
        return (null, channelId, sendAt);
    }

    private static string? AnnounceJumpUrl(ulong guildId, Announcement a) =>
        a.MessageIds.Count > 0 ? $"https://discord.com/channels/{guildId}/{a.ChannelId}/{a.MessageIds[0]}" : null;

    private static object AnnounceDto(SocketGuild guild, Announcement a)
    {
        var channel = guild.GetTextChannel(a.ChannelId);
        return new
        {
            id = a.Id,
            channelId = a.ChannelId.ToString(),
            channel = channel?.Name,
            voice = channel is SocketVoiceChannel,
            content = a.Content,
            allowMentions = a.AllowMentions,
            sendAt = a.SendAt?.ToUnixTimeMilliseconds(),
            sentAt = a.SentAt?.ToUnixTimeMilliseconds(),
            status = a.Status,
            error = a.Error,
            messages = a.Status is "sent" or "failed" ? a.MessageIds.Count : PostSplitter.Split(a.Content).Count,
            jumpUrl = AnnounceJumpUrl(guild.Id, a),
            createdBy = Member(guild, a.CreatedBy)
        };
    }
}

public sealed record AnnounceBody(string? ChannelId, string? Content, bool AllowMentions = false, string? Date = null, string? Time = null);
