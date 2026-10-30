using System.Globalization;
using System.Security.Claims;
using Discord;
using Discord.WebSocket;
using LfgBot.Models;
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
        MapAnnounceTemplates(g);

        // Как текст разобьётся на сообщения Discord
        g.MapPost("/preview", (AnnounceBody body) =>
        {
            var text = (body.Content ?? "").Trim();
            if (text.Length == 0) return Error("Write the text of the post.");
            if (text.Length > PostSplitter.MaxTotalLength) return Error($"The post is limited to {PostSplitter.MaxTotalLength} characters.");
            var blocks = PostSplitter.Split(AnnouncementText.Compose(text, ParseIds(body.PingRoleIds), ParseIds(body.PingUserIds), Everyone(body.PingEveryone)));
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
                CreatedBy = user.UserId()!.Value,
                PingRoleIds = ParseIds(body.PingRoleIds),
                PingUserIds = ParseIds(body.PingUserIds),
                PingEveryone = Everyone(body.PingEveryone)
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
                a.PingRoleIds = ParseIds(body.PingRoleIds);
                a.PingUserIds = ParseIds(body.PingUserIds);
                a.PingEveryone = Everyone(body.PingEveryone);
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

    private static void MapAnnounceTemplates(RouteGroupBuilder g)
    {
        g.MapGet("/templates", (ulong guildId, DiscordSocketClient client, AnnouncementTemplateStore templates) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            return Results.Ok(templates.Get(guildId).Select(t => new
            {
                key = t.Key,
                name = t.Name,
                channelId = t.ChannelId?.ToString(),
                channel = t.ChannelId is { } c ? guild.GetTextChannel(c)?.Name : null,
                content = t.Content,
                allowMentions = t.AllowMentions,
                pingRoles = t.PingRoleIds.Select(id => Role(guild, id)),
                pingUsers = t.PingUserIds.Select(id => Member(guild, id)),
                pingEveryone = t.PingEveryone
            }));
        });

        // key = "new" — новый шаблон
        g.MapPut("/templates/{key}", (ulong guildId, string key, AnnounceTemplateBody body, DiscordSocketClient client,
            AnnouncementTemplateStore templates) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var name = (body.Name ?? "").Trim();
            if (name.Length is 0 or > 80) return Error("Template name: 1–80 characters.");
            var text = (body.Content ?? "").Trim();
            if (text.Length > PostSplitter.MaxTotalLength) return Error($"The text is limited to {PostSplitter.MaxTotalLength} characters.");
            ulong? channelId = ParseId(body.ChannelId) is { } c && guild.GetTextChannel(c) is not null ? c : null;
            var error = templates.Save(new AnnouncementTemplate
            {
                Key = key == "new" ? IdGen.New() : key,
                GuildId = guildId,
                Name = name,
                ChannelId = channelId,
                Content = text,
                AllowMentions = body.AllowMentions,
                PingRoleIds = ParseIds(body.PingRoleIds).Where(id => guild.GetRole(id) is not null).ToList(),
                PingUserIds = ParseIds(body.PingUserIds),
                PingEveryone = Everyone(body.PingEveryone)
            });
            return error is null ? Results.Ok() : Error(error);
        });

        g.MapDelete("/templates/{key}", (ulong guildId, string key, AnnouncementTemplateStore templates) =>
            templates.Remove(guildId, key) ? Results.Ok() : Error("Template not found."));
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
        var canMentionAll = guild.CurrentUser.GetPermissions(channel).MentionEveryone;
        if (!canMentionAll && (Everyone(body.PingEveryone) is not null || (body.AllowMentions && text.Contains("@everyone"))))
            return ("The bot can't ping @everyone / @here in this channel (Mention Everyone permission).", 0, null);
        foreach (var roleId in ParseIds(body.PingRoleIds))
        {
            if (guild.GetRole(roleId) is not { } role) return ("A role to ping no longer exists.", 0, null);
            if (!role.IsMentionable && !canMentionAll)
                return ($"@{role.Name} can't be pinged by the bot: make the role mentionable or give the bot Mention Everyone.", 0, null);
        }
        if (ParseIds(body.PingRoleIds).Count + ParseIds(body.PingUserIds).Count > 50) return ("Up to 50 roles and people to ping.", 0, null);

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
            messages = a.Status is "sent" or "failed" ? a.MessageIds.Count : PostSplitter.Split(a.FullText()).Count,
            jumpUrl = AnnounceJumpUrl(guild.Id, a),
            createdBy = Member(guild, a.CreatedBy),
            pingRoles = a.PingRoleIds.Select(id => Role(guild, id)),
            pingUsers = a.PingUserIds.Select(id => Member(guild, id)),
            pingEveryone = a.PingEveryone
        };
    }

    private static string? Everyone(string? value) => value is "everyone" or "here" ? value : null;
}

public sealed record AnnounceBody(string? ChannelId, string? Content, bool AllowMentions = false, string? Date = null, string? Time = null,
    List<string>? PingRoleIds = null, List<string>? PingUserIds = null, string? PingEveryone = null);
public sealed record AnnounceTemplateBody(string? Name, string? ChannelId, string? Content, bool AllowMentions = false,
    List<string>? PingRoleIds = null, List<string>? PingUserIds = null, string? PingEveryone = null);
