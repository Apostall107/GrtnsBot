using Discord.WebSocket;
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
    public sealed record PageBlockBody(string Type, string? Text, string? RolePanelId, string? ModuleKey);
    public sealed record PageBody(string? Name, List<PageBlockBody>? Blocks);
    public sealed record PagePublishBody(string? ChannelId);

    private static object BlockDto(PageBlock b) => new
    {
        id = b.Id, type = b.Type, text = b.Text, rolePanelId = b.RolePanelId, moduleKey = b.ModuleKey,
        messages = b.MessageIds.Count
    };

    private static object PageDto(ChannelPage p) => new
    {
        id = p.Id, name = p.Name, channelId = p.ChannelId?.ToString(),
        published = p.ChannelId is not null, blockCount = p.Blocks.Count,
        blocks = p.Blocks.Select(BlockDto)
    };

    private static void MapPages(RouteGroupBuilder g)
    {
        // Список страниц + готовые панели ролей и доступные панели модулей — для редактора
        g.MapGet("/", (ulong guildId, DiscordSocketClient client, [FromServices] ChannelPageStore store,
            [FromServices] CommunityStore community) =>
        {
            var guild = client.GetGuild(guildId);
            var rolePanels = community.GetPanels(guildId)
                .Select(rp => new { id = rp.Id, title = string.IsNullOrWhiteSpace(rp.Title) ? "(no title)" : rp.Title });
            var pages = store.Get(guildId).Select(p => new
            {
                id = p.Id, name = p.Name, published = p.ChannelId is not null,
                channel = p.ChannelId is { } c ? guild?.GetTextChannel(c)?.Name : null,
                blockCount = p.Blocks.Count
            });
            return Results.Ok(new { pages, rolePanels, moduleKeys = ChannelPageService.ModuleKeys });
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
            var page = new ChannelPage { GuildId = guildId, Name = Clean(body.Name) ?? "Channel page" };
            if (store.Save(page) is { } error) return Error(error);
            return Results.Ok(PageDto(page));
        });

        g.MapPut("/{id}", (ulong guildId, string id, PageBody body, [FromServices] ChannelPageStore store) =>
        {
            if (store.Find(id) is not { } page || page.GuildId != guildId) return GuildNotFound();
            page.Name = Clean(body.Name) ?? page.Name;
            // перенести ID сообщений по id блока, чтобы правка текста не теряла привязку до переиздания
            var old = page.Blocks.ToDictionary(b => b.Id, b => b.MessageIds);
            page.Blocks = (body.Blocks ?? []).Select(b => new PageBlock
            {
                Type = b.Type is "text" or "rolepanel" or "modulepanel" ? b.Type : "text",
                Text = b.Type == "text" ? b.Text : null,
                RolePanelId = b.Type == "rolepanel" ? b.RolePanelId : null,
                ModuleKey = b.Type == "modulepanel" && ChannelPageService.ModuleKeys.Contains(b.ModuleKey) ? b.ModuleKey : null
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
            if (await service.PublishAsync(id, channelId) is { } error) return Error(error);
            return Results.Ok(new { ok = true });
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
