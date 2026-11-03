using System.Security.Claims;
using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

// Два модуля: Welcome (приветствие + автороль) и Role panels (самовыдача ролей)
public static partial class PanelApi
{
    // ================= Welcome =================

    private static void MapWelcome(RouteGroupBuilder g)
    {
        g.MapGet("/", (ulong guildId, DiscordSocketClient client, CommunityStore store, CommunityService community) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var w = store.GetWelcome(guildId);
            return Results.Ok(new
            {
                channelId = w.ChannelId?.ToString(),
                channel = MessageDto(w.Channel),
                dm = MessageDto(w.Dm),
                applyButton = w.ApplyButton,
                applyLabel = w.ApplyLabel,
                links = w.Links.Select(l => new { label = l.Label, url = l.Url, emoji = l.Emoji }),
                autoRoleMode = w.AutoRoleMode,
                autoRoles = w.AutoRoleIds.Select(id => CommunityRole(guild, id)),
                autoRoleDelay = w.AutoRoleDelaySeconds,
                skipBots = w.SkipBots,
                ticketPanelUrl = community.TicketPanelUrl(guildId),
                canManageRoles = guild.CurrentUser.GuildPermissions.ManageRoles,
                server = new { name = guild.Name, members = guild.MemberCount, iconUrl = guild.IconUrl },
                defaults = new { channel = MessageDto(WelcomeDefaults.ChannelMessage()), dm = MessageDto(WelcomeDefaults.DmMessage()) }
            });
        });

        g.MapPut("/", (ulong guildId, WelcomeBody body, DiscordSocketClient client, CommunityStore store) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var (settings, error) = ParseWelcome(guild, body);
            if (error is not null) return Error(error);
            store.SetWelcome(settings!);
            return Results.Ok();
        });

        // Тест: приветствие (из формы, даже не сохранённое) приходит в ЛС тому, кто нажал
        g.MapPost("/test", async (ulong guildId, WelcomeBody body, ClaimsPrincipal user, DiscordSocketClient client, CommunityService community) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            if (user.UserId() is not { } me || guild.GetUser(me) is not { } member) return Error("You are not on this server.");
            var (settings, error) = ParseWelcome(guild, body, forTest: true);
            if (error is not null) return Error(error);
            var sendError = await community.SendTestAsync(member, settings);
            return sendError is null ? Results.Ok() : Error(sendError);
        });

        g.MapGet("/emojis", (ulong guildId, DiscordSocketClient client) => EmojisAsync(guildId, client));
    }

    private static object MessageDto(WelcomeMessage m) => new
    {
        enabled = m.Enabled,
        content = m.Content,
        useEmbed = m.UseEmbed,
        title = m.Title,
        description = m.Description,
        color = m.Color,
        showAvatar = m.ShowAvatar,
        imageUrl = m.ImageUrl,
        footer = m.Footer
    };

    private static (WelcomeSettings? Settings, string? Error) ParseWelcome(SocketGuild guild, WelcomeBody body, bool forTest = false)
    {
        var channelId = ParseId(body.ChannelId);
        if (channelId is { } c && guild.GetTextChannel(c) is null) return (null, "Channel not found.");

        var (channel, chErr) = ParseMessage(body.Channel, "Channel message");
        if (chErr is not null) return (null, chErr);
        var (dm, dmErr) = ParseMessage(body.Dm, "DM");
        if (dmErr is not null) return (null, dmErr);
        if (channel!.Enabled && channelId is null && !forTest) return (null, "Choose the channel for the welcome message or turn it off.");

        var links = new List<WelcomeLink>();
        foreach (var l in body.Links ?? [])
        {
            if (string.IsNullOrWhiteSpace(l.Label) && string.IsNullOrWhiteSpace(l.Url)) continue;
            if (string.IsNullOrWhiteSpace(l.Label)) return (null, "Every link button needs a label.");
            if (!CommunityService.IsUrl(l.Url)) return (null, $"“{l.Label}”: the link must start with https://");
            links.Add(new WelcomeLink { Label = Cut(l.Label.Trim(), 80), Url = l.Url!.Trim(), Emoji = Blank(l.Emoji) });
        }
        if (links.Count > WelcomeDefaults.MaxLinks) return (null, $"Up to {WelcomeDefaults.MaxLinks} link buttons.");

        var mode = body.AutoRoleMode == WelcomeDefaults.AutoRoleJoin ? WelcomeDefaults.AutoRoleJoin : WelcomeDefaults.AutoRoleOff;
        var roles = ParseIds(body.AutoRoleIds).Distinct().ToList();
        if (roles.Count > WelcomeDefaults.MaxAutoRoles) return (null, $"Up to {WelcomeDefaults.MaxAutoRoles} auto roles.");
        if (mode == WelcomeDefaults.AutoRoleJoin && !forTest)
        {
            if (roles.Count == 0) return (null, "Pick at least one auto role or turn the auto role off.");
            if (RoleProblem(guild, roles) is { } problem) return (null, problem);
        }
        if (body.AutoRoleDelay is < 0 or > 600) return (null, "Auto role delay: 0–600 seconds.");

        return (new WelcomeSettings
        {
            GuildId = guild.Id,
            ChannelId = channelId,
            Channel = channel,
            Dm = dm!,
            ApplyButton = body.ApplyButton,
            ApplyLabel = Blank(body.ApplyLabel) is { } al ? Cut(al, 80) : null,
            Links = links,
            AutoRoleMode = mode,
            AutoRoleIds = roles,
            AutoRoleDelaySeconds = body.AutoRoleDelay ?? 0,
            SkipBots = body.SkipBots
        }, null);
    }

    private static (WelcomeMessage? Message, string? Error) ParseMessage(WelcomeMessageBody? b, string what)
    {
        if (b is null) return (new WelcomeMessage(), null);
        if ((b.Content?.Length ?? 0) > WelcomeDefaults.MaxText) return (null, $"{what}: the text is limited to {WelcomeDefaults.MaxText} characters.");
        if ((b.Description?.Length ?? 0) > 4000) return (null, $"{what}: the card text is limited to 4000 characters.");
        if ((b.Title?.Length ?? 0) > 256) return (null, $"{what}: the title is limited to 256 characters.");
        if (Blank(b.Color) is { } color && Tx.ParseColor(color) is null) return (null, $"{what}: the color must be a #hex code.");
        if (Blank(b.ImageUrl) is { } img && !CommunityService.IsUrl(img)) return (null, $"{what}: the image must be an https:// link.");
        var m = new WelcomeMessage
        {
            Enabled = b.Enabled,
            Content = Blank(b.Content),
            UseEmbed = b.UseEmbed,
            Title = Blank(b.Title),
            Description = Blank(b.Description),
            Color = Blank(b.Color),
            ShowAvatar = b.ShowAvatar,
            ImageUrl = Blank(b.ImageUrl),
            Footer = Blank(b.Footer) is { } f ? Cut(f, 2048) : null
        };
        if (m.Enabled && m.Content is null && !(m.UseEmbed && (m.Title ?? m.Description ?? m.ImageUrl) is not null))
            return (null, $"{what}: write a text or fill in the card.");
        return (m, null);
    }

    // ================= Role panels =================

    private static void MapRolePanels(RouteGroupBuilder g)
    {
        g.MapGet("/panels", (ulong guildId, DiscordSocketClient client, CommunityStore store, CommunityService community) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            return Results.Ok(new
            {
                panels = store.GetPanels(guildId).Select(p => PanelDto(guild, p, community)),
                canManageRoles = guild.CurrentUser.GuildPermissions.ManageRoles,
                botRole = guild.CurrentUser.Roles.OrderByDescending(r => r.Position).FirstOrDefault() is { } top ? top.Name : null
            });
        });

        g.MapPut("/panels/{id}/sticky", (ulong guildId, string id, StickyBody body, CommunityStore store) =>
            store.FindPanel(id) is { } p && p.GuildId == guildId && store.SetSticky(id, body.Sticky) ? Results.Ok() : Error("Panel not found."));

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
            if (FillPanel(guild, panel, body) is { } error) return Error(error);
            store.SavePanel(panel);
            // Опубликованная панель обновляется сразу (кнопки, список, реакции)
            if (panel.MessageId is not null && await community.RefreshAsync(panel) is { } refreshError)
                return Error($"Saved, but the posted panel wasn't updated: {refreshError}");
            return Results.Ok();
        });

        g.MapPost("/panels/{panelId}/copy", (ulong guildId, string panelId, CommunityStore store) =>
        {
            if (store.FindPanel(panelId) is not { } panel || panel.GuildId != guildId) return Error("This panel no longer exists.");
            panel.Id = IdGen.New();
            panel.Title = $"{panel.Title} (copy)";
            panel.ChannelId = null;
            panel.MessageId = null;
            foreach (var b in panel.Bindings) b.Id = IdGen.New();
            return store.SavePanel(panel) is { } limit ? Error(limit) : Results.Ok(new { id = panel.Id });
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

        g.MapGet("/emojis", (ulong guildId, DiscordSocketClient client) => EmojisAsync(guildId, client));
    }

    private static async Task<IResult> EmojisAsync(ulong guildId, DiscordSocketClient client)
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
        var ids = roleIds.ToList();
        if (ids.Any(id => guild.GetRole(id) is null)) return "Some roles no longer exist.";
        var bad = ids.Select(guild.GetRole).Where(r => !CommunityService.CanManage(guild, r)).Select(r => "@" + r!.Name).Distinct().ToList();
        return bad.Count == 0 ? null
            : $"The bot can't give {string.Join(", ", bad)}: drag the bot's role above them in Server Settings → Roles (managed/bot roles can't be given).";
    }

    private static string? FillPanel(SocketGuild guild, RolePanel panel, RolePanelBody body)
    {
        var title = Blank(body.Title);
        var text = Blank(body.Text);
        if ((title?.Length ?? 0) > 256) return "The title is limited to 256 characters.";
        if ((text?.Length ?? 0) > 3000) return "The text is limited to 3000 characters.";
        if (!Enum.TryParse<RolePanelMode>(body.Mode, true, out var mode)) return "Choose the panel mode.";
        var color = Blank(body.Color);
        if (color is not null && Tx.ParseColor(color) is null) return "The color must be a #hex code like #5865f2.";
        if (Blank(body.ImageUrl) is { } img && !CommunityService.IsUrl(img)) return "The image must be an https:// link.";
        if (Blank(body.ThumbnailUrl) is { } th && !CommunityService.IsUrl(th)) return "The thumbnail must be an https:// link.";
        if (body.MaxPick is < 0 or > 25) return "Max picks in the list: 0–25.";

        var bindings = new List<RoleBinding>();
        foreach (var (b, i) in (body.Bindings ?? []).Select((b, i) => (b, i + 1)))
        {
            var roles = ParseIds(b.RoleIds).Distinct().ToList();
            if (roles.Count == 0 && string.IsNullOrWhiteSpace(b.Label) && string.IsNullOrWhiteSpace(b.Emoji)) continue; // пустая строка
            if (!Enum.TryParse<RoleTrigger>(b.Trigger, true, out var trigger)) return $"#{i}: choose button, reaction or list item.";
            if (roles.Count == 0) return $"#{i}: pick at least one role it gives.";
            if (roles.Count > RolePanel.MaxRolesPerBinding) return $"#{i}: up to {RolePanel.MaxRolesPerBinding} roles.";
            var emoji = Blank(b.Emoji);
            if (trigger == RoleTrigger.Reaction && emoji is null) return $"#{i}: a reaction needs an emoji.";
            if (trigger == RoleTrigger.Button && emoji is null && Blank(b.Label) is null && guild.GetRole(roles[0]) is null)
                return $"#{i}: a button needs a label or an emoji.";
            Enum.TryParse<RoleButtonColor>(b.Color, true, out var bc);
            bindings.Add(new RoleBinding
            {
                // Существующий id сохраняется — опубликованные кнопки продолжают работать
                Id = Blank(b.Id) is { } id && panel.Bindings.Any(x => x.Id == id) ? id : IdGen.New(),
                Trigger = trigger,
                Label = Blank(b.Label) is { } l ? Cut(l, 80) : null,
                Emoji = emoji,
                Description = Blank(b.Description) is { } d ? Cut(d, 100) : null,
                Color = bc,
                RoleIds = roles
            });
        }

        if (bindings.Count == 0) return "Add at least one button, reaction or list item.";
        if (bindings.Count > RolePanel.MaxBindings) return $"Up to {RolePanel.MaxBindings} items on one panel.";
        var buttons = bindings.Count(b => b.Trigger == RoleTrigger.Button);
        var options = bindings.Count(b => b.Trigger == RoleTrigger.Option);
        var reactions = bindings.Where(b => b.Trigger == RoleTrigger.Reaction).ToList();
        if (buttons > (options > 0 ? 20 : 25)) return options > 0 ? "With a dropdown list the panel fits up to 20 buttons." : "Up to 25 buttons.";
        if (reactions.Count > 20) return "Up to 20 reactions on a message.";
        if (reactions.Select(r => r.Emoji).Distinct().Count() != reactions.Count) return "Every reaction needs its own emoji.";
        if (RoleProblem(guild, bindings.SelectMany(b => b.RoleIds)) is { } problem) return problem;
        var required = ParseIds(body.RequiredRoleIds).Where(id => guild.GetRole(id) is not null).Distinct().Take(10).ToList();

        panel.Title = title;
        panel.Text = text;
        panel.Color = color;
        panel.ImageUrl = Blank(body.ImageUrl);
        panel.ThumbnailUrl = Blank(body.ThumbnailUrl);
        panel.Footer = Blank(body.Footer) is { } f ? Cut(f, 2048) : null;
        panel.ShowList = body.ShowList;
        panel.ShowHint = body.ShowHint;
        panel.Mode = mode;
        panel.Placeholder = Blank(body.Placeholder) is { } ph ? Cut(ph, 150) : null;
        panel.MaxPick = body.MaxPick ?? 0;
        panel.RequiredRoleIds = required;
        panel.Bindings = bindings;
        if (title is null && text is null && !panel.ShowList && panel.ImageUrl is null) return "Write a title or a text (or show the list of roles).";
        return null;
    }

    private static object PanelDto(SocketGuild guild, RolePanel p, CommunityService community) => new
    {
        id = p.Id,
        title = p.Title,
        text = p.Text,
        color = p.Color,
        imageUrl = p.ImageUrl,
        thumbnailUrl = p.ThumbnailUrl,
        footer = p.Footer,
        showList = p.ShowList,
        showHint = p.ShowHint,
        mode = p.Mode.ToString(),
        placeholder = p.Placeholder,
        maxPick = p.MaxPick,
        requiredRoles = p.RequiredRoleIds.Select(id => CommunityRole(guild, id)),
        bindings = p.Bindings.Select(b => new
        {
            id = b.Id,
            trigger = b.Trigger.ToString(),
            label = b.Label,
            shownLabel = community.BindingLabel(p, b),
            emoji = b.Emoji,
            description = b.Description,
            color = b.Color.ToString(),
            roles = b.RoleIds.Select(id => CommunityRole(guild, id))
        }),
        channel = p.ChannelId is { } c ? guild.GetTextChannel(c)?.Name : null,
        channelId = p.ChannelId?.ToString(),
        jumpUrl = p.ChannelId is { } ch && p.MessageId is { } m ? $"https://discord.com/channels/{guild.Id}/{ch}/{m}" : null,
        sticky = p.Sticky
    };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}

public sealed record WelcomeMessageBody(bool Enabled, string? Content, bool UseEmbed, string? Title, string? Description,
    string? Color, bool ShowAvatar, string? ImageUrl, string? Footer);
public sealed record WelcomeLinkBody(string? Label, string? Url, string? Emoji);
public sealed record WelcomeBody(string? ChannelId, WelcomeMessageBody? Channel, WelcomeMessageBody? Dm, bool ApplyButton = true,
    string? ApplyLabel = null, List<WelcomeLinkBody>? Links = null, string? AutoRoleMode = null, List<string>? AutoRoleIds = null,
    int? AutoRoleDelay = 0, bool SkipBots = true);
public sealed record RoleBindingBody(string? Id, string? Trigger, string? Label, string? Emoji, string? Description, string? Color,
    List<string>? RoleIds);
public sealed record RolePanelBody(string? Title, string? Text, string? Color, string? ImageUrl, string? ThumbnailUrl, string? Footer,
    bool ShowList, bool ShowHint, string? Mode, string? Placeholder, int? MaxPick, List<string>? RequiredRoleIds,
    List<RoleBindingBody>? Bindings, string? ChannelId = null);
