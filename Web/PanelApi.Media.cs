using Discord;
using Discord.WebSocket;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

/// <summary>
/// Медиатека: картинки и гифки для постов. Файлы живут вложениями в служебном канале Discord — сервер бота их не хранит.
/// </summary>
public static partial class PanelApi
{
    public sealed record MediaChannelBody(string? ChannelId, bool Create = false);
    public sealed record MediaUrlBody(string? Url, string? Name);
    public sealed record MediaRenameBody(string? Name);

    private static object MediaDto(MediaItem i) => new
    {
        id = i.MessageId.ToString(), name = i.Name, token = MediaService.Token(i.Name), url = i.Url, file = i.FileName,
        size = i.Size, width = i.Width, height = i.Height, gif = i.FileName.EndsWith(".gif", StringComparison.OrdinalIgnoreCase),
        at = i.At.ToUnixTimeMilliseconds(), byBot = i.ByBot
    };

    private static void MapMedia(RouteGroupBuilder g)
    {
        g.MapGet("/", async (ulong guildId, bool? fresh, DiscordSocketClient client, [FromServices] MediaService media) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            var channel = media.Channel(guildId);
            var items = channel is null ? [] : await media.ListAsync(guildId, fresh == true);
            return Results.Ok(new
            {
                channelId = channel?.Id.ToString(), channel = channel?.Name,
                maxMb = MediaService.MaxBytes / 1024 / 1024,
                items = items.Select(MediaDto)
            });
        });

        // Служебный канал: выбрать существующий или создать закрытый (виден только боту и админам)
        g.MapPut("/channel", async (ulong guildId, MediaChannelBody body, DiscordSocketClient client,
            [FromServices] GuildPrefsStore prefs, [FromServices] MediaService media) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            ulong? id;
            if (body.Create)
            {
                try
                {
                    var created = await guild.CreateTextChannelAsync("bot-media", c =>
                    {
                        c.Topic = "Images and GIFs for the bot's posts (media library). Don't post anything else here.";
                        c.PermissionOverwrites = new List<Overwrite>
                        {
                            new(guild.EveryoneRole.Id, PermissionTarget.Role, new OverwritePermissions(viewChannel: PermValue.Deny)),
                            new(guild.CurrentUser.Id, PermissionTarget.User, new OverwritePermissions(viewChannel: PermValue.Allow,
                                sendMessages: PermValue.Allow, attachFiles: PermValue.Allow, readMessageHistory: PermValue.Allow,
                                manageMessages: PermValue.Allow))
                        };
                    });
                    id = created.Id;
                }
                catch (Discord.Net.HttpException)
                {
                    return Error("I can't create channels — give the bot Manage Channels, or choose an existing channel.");
                }
            }
            else
            {
                id = ParseId(body.ChannelId);
                if (id is { } cid && guild.GetTextChannel(cid) is not { } ch) return Error("Channel not found.");
            }
            prefs.Update(guildId, p => p.MediaChannelId = id);
            media.Invalidate(guildId);
            return Results.Ok(new { channelId = id?.ToString() });
        });

        // Загрузка файла: тело запроса — сам файл, имя файла и желаемое имя — в заголовках
        g.MapPost("/upload", async (ulong guildId, HttpRequest request, DiscordSocketClient client, [FromServices] MediaService media) =>
        {
            if (client.GetGuild(guildId) is null) return GuildNotFound();
            if (request.ContentLength > MediaService.MaxBytes) return Error($"The file is larger than {MediaService.MaxBytes / 1024 / 1024} MB.");
            using var ms = new MemoryStream();
            await request.Body.CopyToAsync(ms);
            if (ms.Length > MediaService.MaxBytes) return Error($"The file is larger than {MediaService.MaxBytes / 1024 / 1024} MB.");
            var fileName = Uri.UnescapeDataString(request.Headers["X-File-Name"].ToString() is { Length: > 0 } f ? f : "image.png");
            var name = Uri.UnescapeDataString(request.Headers["X-Media-Name"].ToString());
            var (item, error) = await media.UploadAsync(guildId, name, fileName, ms.ToArray());
            return error is not null ? Error(error) : Results.Ok(item is null ? new { } : MediaDto(item));
        });

        g.MapPost("/url", async (ulong guildId, MediaUrlBody body, DiscordSocketClient client, [FromServices] MediaService media) =>
        {
            if (client.GetGuild(guildId) is null) return GuildNotFound();
            if (string.IsNullOrWhiteSpace(body.Url)) return Error("Paste a link.");
            var (item, error) = await media.AddFromUrlAsync(guildId, string.IsNullOrWhiteSpace(body.Name) ? null : body.Name, body.Url);
            return error is not null ? Error(error) : Results.Ok(item is null ? new { } : MediaDto(item));
        });

        g.MapPut("/{id}", async (ulong guildId, string id, MediaRenameBody body, [FromServices] MediaService media) =>
            ParseId(id) is not { } mid ? Error("Unknown image.")
            : await media.RenameAsync(guildId, mid, body.Name ?? "") is { } error ? Error(error) : Results.Ok());

        g.MapDelete("/{id}", async (ulong guildId, string id, [FromServices] MediaService media) =>
            ParseId(id) is not { } mid ? Error("Unknown image.")
            : await media.DeleteAsync(guildId, mid) is { } error ? Error(error) : Results.Ok());
    }
}
