using Discord.WebSocket;
using LfgBot.Models;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

/// <summary>
/// Страницы каналов: сборка поста из нескольких сообщений бота и панелей (ролей/модулей),
/// публикуемых в один канал по порядку как единое целое.
/// </summary>
public static partial class PanelApi
{
    public sealed record PageBlockBody(string? Id, string Type, string? Text, string? RolePanelId, string? ModuleKey);
    public sealed record PageBody(string? Name, List<PageBlockBody>? Blocks, string? ProjectKey = null);
    public sealed record PagePublishBody(string? ChannelId, bool? Full);

    private static object BlockDto(PageBlock b) => new
    {
        id = b.Id, type = b.Type, text = b.Text, rolePanelId = b.RolePanelId, moduleKey = b.ModuleKey,
        messages = b.MessageIds.Count
    };

    private static object PageDto(ChannelPage p) => new
    {
        id = p.Id, name = p.Name, projectKey = p.ProjectKey, channelId = p.ChannelId?.ToString(),
        published = p.ChannelId is not null, blockCount = p.Blocks.Count,
        blocks = p.Blocks.Select(BlockDto)
    };

    private static void MapPages(RouteGroupBuilder g)
    {
        // Эмодзи сервера и бота — для выбора в текстовых блоках и превью (:name: → картинка)
        g.MapGet("/emojis", (ulong guildId, DiscordSocketClient client) => EmojisAsync(guildId, client));

        // Список страниц + готовые панели ролей и доступные панели модулей — для редактора
        g.MapGet("/", (ulong guildId, DiscordSocketClient client, [FromServices] ChannelPageStore store,
            [FromServices] CommunityStore community, [FromServices] LfgProjectStore lfgProjects) =>
        {
            var guild = client.GetGuild(guildId);
            var rolePanels = community.GetPanels(guildId)
                .Select(rp => new { id = rp.Id, title = string.IsNullOrWhiteSpace(rp.Title) ? "(no title)" : rp.Title });
            var pages = store.Get(guildId).Select(p => new
            {
                id = p.Id, name = p.Name, published = p.ChannelId is not null, projectKey = p.ProjectKey,
                channelId = p.ChannelId?.ToString(),
                channel = p.ChannelId is { } c ? guild?.GetTextChannel(c)?.Name : null,
                blockCount = p.Blocks.Count
            });
            var groups = lfgProjects.GetProjects(guildId).Select(p => new { key = p.Key, name = p.Name });
            return Results.Ok(new { pages, rolePanels, moduleKeys = ChannelPageService.ModuleKeys, groups });
        });

        g.MapGet("/{id}", (ulong guildId, string id, DiscordSocketClient client, [FromServices] ChannelPageStore store,
            [FromServices] CommunityStore community) =>
        {
            if (store.Find(id) is not { } page || page.GuildId != guildId) return GuildNotFound();
            var rolePanels = community.GetPanels(guildId)
                .Select(rp => new { id = rp.Id, title = string.IsNullOrWhiteSpace(rp.Title) ? "(no title)" : rp.Title });
            return Results.Ok(new { page = PageDto(page), rolePanels, moduleKeys = ChannelPageService.ModuleKeys });
        });

        g.MapPost("/", (ulong guildId, PageBody body, [FromServices] ChannelPageStore store) =>
        {
            var page = new ChannelPage { GuildId = guildId, Name = Clean(body.Name) ?? "Channel page",
                ProjectKey = string.IsNullOrWhiteSpace(body.ProjectKey) ? null : body.ProjectKey };
            if (store.Save(page) is { } error) return Error(error);
            return Results.Ok(PageDto(page));
        });

        g.MapPut("/{id}", (ulong guildId, string id, PageBody body, [FromServices] ChannelPageStore store) =>
        {
            if (store.Find(id) is not { } page || page.GuildId != guildId) return GuildNotFound();
            page.Name = Clean(body.Name) ?? page.Name;
            if (body.ProjectKey is not null) page.ProjectKey = body.ProjectKey.Length == 0 ? null : body.ProjectKey;
            // Блоки с прежним id сохраняют свои сообщения в канале — следующая публикация обновит их на месте
            var old = page.Blocks.ToDictionary(b => b.Id);
            page.Blocks = (body.Blocks ?? []).Select(b =>
            {
                var type = b.Type is "text" or "rolepanel" or "modulepanel" ? b.Type : "text";
                var prev = b.Id is { } bid && old.TryGetValue(bid, out var o) && o.Type == type ? o : null;
                return new PageBlock
                {
                    Id = prev?.Id ?? IdGen.New(),
                    Type = type,
                    Text = type == "text" ? b.Text : null,
                    RolePanelId = type == "rolepanel" ? b.RolePanelId : null,
                    ModuleKey = type == "modulepanel" && ChannelPageService.ModuleKeys.Contains(b.ModuleKey) ? b.ModuleKey : null,
                    MessageIds = prev?.MessageIds ?? [],
                    PostedKey = prev?.PostedKey
                };
            }).ToList();
            if (store.Save(page) is { } error) return Error(error);
            return Results.Ok(PageDto(page));
        });

        g.MapDelete("/{id}", async (ulong guildId, string id, [FromServices] ChannelPageStore store,
            [FromServices] ChannelPageService service) =>
        {
            if (store.Find(id) is not { } page || page.GuildId != guildId) return GuildNotFound();
            await service.UnpublishAsync(id); // снять сообщения, если были
            store.Delete(id);
            return Results.Ok(new { ok = true });
        });

        g.MapPost("/{id}/publish", async (ulong guildId, string id, PagePublishBody body,
            [FromServices] ChannelPageStore store, [FromServices] ChannelPageService service) =>
        {
            if (store.Find(id) is not { } page || page.GuildId != guildId) return GuildNotFound();
            if (ParseId(body.ChannelId) is not { } channelId) return Error("Choose a channel to publish to.");
            if (await service.PublishAsync(id, channelId, body.Full == true) is { } error) return Error(error);
            return Results.Ok(new { ok = true });
        });

        // Что произойдёт при публикации: какие блоки останутся, поправятся на месте или перепостятся
        g.MapGet("/{id}/plan", async (ulong guildId, string id, string? channelId, [FromServices] ChannelPageStore store,
            [FromServices] ChannelPageService service, [FromServices] CommunityStore community) =>
        {
            if (store.Find(id) is not { } page || page.GuildId != guildId) return GuildNotFound();
            if (ParseId(channelId) is not { } cid) return Error("Choose a channel.");
            var plan = await service.PlanAsync(page, cid);
            var panelsById = community.GetPanels(guildId).ToDictionary(p => p.Id);
            string Label(PageBlock b, int i) => $"#{i + 1} " + (b.Type switch
            {
                "text" => "Text: " + Tx.Clip((b.Text ?? "").ReplaceLineEndings(" ").Trim(), 40),
                "rolepanel" => "Role panel: " + (b.RolePanelId is { } rp && panelsById.TryGetValue(rp, out var panel) && !string.IsNullOrWhiteSpace(panel.Title)
                    ? panel.Title : "(no title)"),
                _ => "Module panel: " + b.ModuleKey
            });
            var labels = page.Blocks.Select((b, i) => new { id = b.Id, label = Label(b, i), type = b.Type }).ToList();
            return Results.Ok(new { full = plan.Full, keep = plan.Keep, edit = plan.Edit, repost = plan.Repost, removed = plan.Removed, blocks = labels });
        });

        g.MapPost("/{id}/unpublish", async (ulong guildId, string id, [FromServices] ChannelPageStore store,
            [FromServices] ChannelPageService service) =>
        {
            if (store.Find(id) is not { } page || page.GuildId != guildId) return GuildNotFound();
            await service.UnpublishAsync(id);
            return Results.Ok(new { ok = true });
        });
    }

    private static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(name.Trim().Length, 80)];
}
