using System.Security.Claims;
using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

// Welcome & roles: приветствие новичков, автороль, панели самовыдачи ролей
public static partial class PanelApi
{
    private static void MapCommunity(RouteGroupBuilder g)
    {
        // ----- Приветствие и автороль -----
        g.MapGet("/welcome", (ulong guildId, DiscordSocketClient client, CommunityStore store, CommunityService community) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var w = store.GetWelcome(guildId);
            return Results.Ok(new
            {
                channelId = w.ChannelId?.ToString(),
                channelText = w.ChannelText,
                sendDm = w.SendDm,
                dmText = w.DmText,
                applyButton = w.ApplyButton,
                autoRoleMode = w.AutoRoleMode,
                autoRoles = w.AutoRoleIds.Select(id => CommunityRole(guild, id)),
                skipBots = w.SkipBots,
                ticketPanelUrl = community.TicketPanelUrl(guildId),
                canManageRoles = guild.CurrentUser.GuildPermissions.ManageRoles,
                defaults = new { channelText = WelcomeDefaults.ChannelText, dmText = WelcomeDefaults.DmText }
            });
        });

        g.MapPut("/welcome", (ulong guildId, WelcomeBody body, DiscordSocketClient client, CommunityStore store) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var channelId = ParseId(body.ChannelId);
            if (channelId is { } c && guild.GetTextChannel(c) is null) return Error("Channel not found.");
            if ((body.ChannelText?.Length ?? 0) > WelcomeDefaults.MaxText || (body.DmText?.Length ?? 0) > WelcomeDefaults.MaxText)
                return Error($"Welcome texts are limited to {WelcomeDefaults.MaxText} characters.");
            if (channelId is not null && string.IsNullOrWhiteSpace(body.ChannelText)) return Error("Write the channel message or turn it off.");
            if (body.SendDm && string.IsNullOrWhiteSpace(body.DmText)) return Error("Write the DM text or turn the DM off.");

            var mode = body.AutoRoleMode == WelcomeDefaults.AutoRoleJoin ? WelcomeDefaults.AutoRoleJoin : WelcomeDefaults.AutoRoleOff;
            var roles = ParseIds(body.AutoRoleIds).Distinct().ToList();
            if (roles.Count > WelcomeDefaults.MaxAutoRoles) return Error($"Up to {WelcomeDefaults.MaxAutoRoles} auto roles.");
            if (mode == WelcomeDefaults.AutoRoleJoin)
            {
                if (roles.Count == 0) return Error("Pick at least one auto role or turn the auto role off.");
                if (RoleProblem(guild, roles) is { } problem) return Error(problem);
            }

            store.SetWelcome(new WelcomeSettings
            {
                GuildId = guildId,
                ChannelId = channelId,
                ChannelText = string.IsNullOrWhiteSpace(body.ChannelText) ? WelcomeDefaults.ChannelText : body.ChannelText.Trim(),
                SendDm = body.SendDm,
                DmText = string.IsNullOrWhiteSpace(body.DmText) ? WelcomeDefaults.DmText : body.DmText.Trim(),
                ApplyButton = body.ApplyButton,
                AutoRoleMode = mode,
                AutoRoleIds = roles,
                SkipBots = body.SkipBots
            });
            return Results.Ok();
        });

        // Тест: приветствие приходит в ЛС тому, кто нажал
        g.MapPost("/welcome/test", async (ulong guildId, ClaimsPrincipal user, DiscordSocketClient client, CommunityService community) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (user.UserId() is not { } me || guild.GetUser(me) is not { } member) return Error("You are not on this server.");
            var error = await community.SendTestAsync(member);
            return error is null ? Results.Ok() : Error(error);
        });

        // ----- Панели ролей -----
        g.MapGet("/panels", (ulong guildId, DiscordSocketClient client, CommunityStore store) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            return Results.Ok(store.GetPanels(guildId).Select(p => PanelDto(guild, p)));
        });

        g.MapPost("/panels", async (ulong guildId, RolePanelBody body, DiscordSocketClient client, CommunityStore store, CommunityService community) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var panel = new RolePanel { GuildId = guildId };
            if (FillPanel(guild, panel, body) is { } error) return Error(error);
            if (store.SavePanel(panel) is { } limit) return Error(limit);
            if (ParseId(body.ChannelId) is { } channelId && await community.PostAsync(panel, channelId) is { } postError)
                return Error($"Saved, but not posted: {postError}");
            return Results.Ok(new { id = panel.Id });
        });

        g.MapPut("/panels/{panelId}", async (ulong guildId, string panelId, RolePanelBody body, DiscordSocketClient client,
            CommunityStore store, CommunityService community) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (store.FindPanel(panelId) is not { } panel || panel.GuildId != guildId) return Error("This panel no longer exists.");
            var oldStyle = panel.Style;
            if (FillPanel(guild, panel, body) is { } error) return Error(error);
            var styleChanged = oldStyle != panel.Style;
            store.SavePanel(panel);

            // Опубликованная панель обновляется сразу; смена вида (кнопки ↔ реакции) — публикуется заново
            if (panel.MessageId is not null && panel.ChannelId is { } channel)
            {
                var refreshError = styleChanged ? await community.PostAsync(panel, channel) : await community.RefreshAsync(panel);
                if (refreshError is not null) return Error($"Saved, but the posted panel wasn't updated: {refreshError}");
            }
            return Results.Ok();
        });

        g.MapPost("/panels/{panelId}/post", async (ulong guildId, string panelId, ChannelBody body, DiscordSocketClient client,
            CommunityStore store, CommunityService community) =>
        {
            if (client.GetGuild(guildId) is null) return GuildNotFound();
            if (store.FindPanel(panelId) is not { } panel || panel.GuildId != guildId) return Error("This panel no longer exists.");
            if (ParseId(body.ChannelId) is not { } channelId) return Error("Choose a channel.");
            var error = await community.PostAsync(panel, channelId);
            return error is null ? Results.Ok() : Error(error);
        });

        g.MapDelete("/panels/{panelId}", async (ulong guildId, string panelId, CommunityStore store, CommunityService community) =>
        {
            if (store.FindPanel(panelId) is not { } panel || panel.GuildId != guildId) return Error("This panel no longer exists.");
            await community.DeletePostedAsync(panel);
            store.RemovePanel(panelId);
            return Results.Ok();
        });

        // Эмодзи сервера и бота — для выбора на панели
        g.MapGet("/emojis", async (ulong guildId, DiscordSocketClient client) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var emojis = guild.Emotes.OrderBy(e => e.Name)
                .Select(e => new EmojiDto(e.Name, $"<{(e.Animated ? "a" : "")}:{e.Name}:{e.Id}>", e.Url, false)).ToList();
            try
            {
                foreach (var e in (await client.GetApplicationEmotesAsync()).OrderBy(e => e.Name))
                    emojis.Add(new EmojiDto(e.Name, $"<{(e.Animated ? "a" : "")}:{e.Name}:{e.Id}>", e.Url, true));
            }
            catch (Exception)
            {
                // нет доступа к эмодзи приложения
            }
            return Results.Ok(emojis);
        });
    }

    private static object CommunityRole(SocketGuild guild, ulong id)
    {
        var role = guild.GetRole(id);
        return new
        {
            id = id.ToString(),
            name = role?.Name ?? "deleted role",
            color = role is null || role.Colors.PrimaryColor.RawValue == 0 ? null : $"#{role.Colors.PrimaryColor.RawValue:x6}",
            exists = role is not null,
            manageable = CommunityService.CanManage(guild, role)
        };
    }

    /// <summary>Роли, которые бот не может выдавать, — текст ошибки (или null).</summary>
    private static string? RoleProblem(SocketGuild guild, IEnumerable<ulong> roleIds)
    {
        if (!guild.CurrentUser.GuildPermissions.ManageRoles) return "The bot needs the Manage Roles permission.";
        var missing = roleIds.Where(id => guild.GetRole(id) is null).ToList();
        if (missing.Count > 0) return "Some roles no longer exist.";
        var bad = roleIds.Select(guild.GetRole).Where(r => !CommunityService.CanManage(guild, r)).Select(r => "@" + r!.Name).ToList();
        return bad.Count == 0 ? null
            : $"The bot can't give {string.Join(", ", bad)}: drag the bot's role above them in Server Settings → Roles (managed/bot roles can't be given).";
    }

    private static string? FillPanel(SocketGuild guild, RolePanel panel, RolePanelBody body)
    {
        var title = body.Title?.Trim() ?? "";
        var text = body.Text?.Trim() ?? "";
        if (title.Length > 256) return "The title is limited to 256 characters.";
        if (text.Length > 3000) return "The text is limited to 3000 characters.";
        if (title.Length == 0 && text.Length == 0) return "Write a title or a text.";
        if (!Enum.TryParse<RolePanelStyle>(body.Style, true, out var style)) return "Choose how roles are picked.";
        if (!Enum.TryParse<RolePanelMode>(body.Mode, true, out var mode)) return "Choose the panel mode.";
        var color = string.IsNullOrWhiteSpace(body.Color) ? null : body.Color.Trim();
        if (color is not null && Tx.ParseColor(color) is null) return "The color must be a #hex code like #5865f2.";

        var options = new List<RolePanelOption>();
        foreach (var o in body.Options ?? [])
        {
            if (ParseId(o.RoleId) is not { } roleId) continue;
            if (options.Any(x => x.RoleId == roleId)) return "Each role can be on the panel only once.";
            var emoji = string.IsNullOrWhiteSpace(o.Emoji) ? null : o.Emoji.Trim();
            options.Add(new RolePanelOption
            {
                RoleId = roleId,
                Label = string.IsNullOrWhiteSpace(o.Label) ? null : o.Label.Trim()[..Math.Min(o.Label.Trim().Length, 80)],
                Emoji = emoji,
                Description = string.IsNullOrWhiteSpace(o.Description) ? null : o.Description.Trim()[..Math.Min(o.Description.Trim().Length, 100)]
            });
        }
        if (options.Count == 0) return "Add at least one role.";
        var max = style == RolePanelStyle.Dropdown ? 25 : RolePanel.MaxOptions;
        if (options.Count > max) return $"Up to {max} roles on one panel.";
        if (style == RolePanelStyle.Reactions)
        {
            if (options.Any(o => o.Emoji is null)) return "With reactions every role needs an emoji.";
            if (options.Select(o => o.Emoji).Distinct().Count() != options.Count) return "With reactions every role needs its own emoji.";
        }
        if (RoleProblem(guild, options.Select(o => o.RoleId)) is { } problem) return problem;

        panel.Title = title;
        panel.Text = text;
        panel.Color = color;
        panel.Style = style;
        panel.Mode = mode;
        panel.Placeholder = string.IsNullOrWhiteSpace(body.Placeholder) ? null : body.Placeholder.Trim()[..Math.Min(body.Placeholder.Trim().Length, 150)];
        panel.Options = options;
        return null;
    }

    private static object PanelDto(SocketGuild guild, RolePanel p) => new
    {
        id = p.Id,
        title = p.Title,
        text = p.Text,
        color = p.Color,
        style = p.Style.ToString(),
        mode = p.Mode.ToString(),
        placeholder = p.Placeholder,
        options = p.Options.Select(o => new
        {
            role = CommunityRole(guild, o.RoleId),
            label = o.Label,
            emoji = o.Emoji,
            description = o.Description
        }),
        channel = p.ChannelId is { } c ? guild.GetTextChannel(c)?.Name : null,
        channelId = p.ChannelId?.ToString(),
        jumpUrl = p.ChannelId is { } ch && p.MessageId is { } m ? $"https://discord.com/channels/{guild.Id}/{ch}/{m}" : null
    };
}

public sealed record WelcomeBody(string? ChannelId, string? ChannelText, bool SendDm, string? DmText, bool ApplyButton = true,
    string? AutoRoleMode = null, List<string>? AutoRoleIds = null, bool SkipBots = true);
public sealed record RolePanelOptionBody(string? RoleId, string? Label, string? Emoji, string? Description);
public sealed record RolePanelBody(string? Title, string? Text, string? Color, string? Style, string? Mode, string? Placeholder,
    List<RolePanelOptionBody>? Options, string? ChannelId = null);
