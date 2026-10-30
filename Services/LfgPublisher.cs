using Discord;
using Discord.WebSocket;
using LfgBot.Models;

namespace LfgBot.Services;

/// <summary>Что опубликовать: из мастера /lfg, с сайта или по расписанию.</summary>
public sealed record LfgPublishRequest(
    ulong GuildId,
    ulong ChannelId,
    ulong OwnerId,
    string ActivityName,
    ulong? PingRoleId,
    IReadOnlyList<SlotRole> Slots,
    DateTimeOffset? StartsAt,
    bool NotifyByDm,
    bool SeatOwner = true,
    IReadOnlyList<ulong>? RequiredRoleIds = null,
    int? MinGs = null,
    string? ProjectKey = null,
    IReadOnlyList<ulong>? MirrorChannelIds = null,
    bool CreateVoice = false);

/// <summary>Публикация сбора: пост с пингом роли, сохранение, ЛС участникам с ролью активности.</summary>
public sealed class LfgPublisher(
    DiscordSocketClient client,
    LfgStore store,
    CharacterStore characters,
    TimeService time,
    LfgNotifier notifier,
    LfgSignupService signup,
    LfgProjectStore projects)
{
    /// <summary>Публикует сбор. Возвращает сбор и ссылку на пост; бросает исключение, если в канал нельзя писать.</summary>
    public async Task<(LfgGroup Group, string JumpUrl, bool DmsStarted)> PublishAsync(LfgPublishRequest r)
    {
        if (await GetChannelAsync(r.ChannelId) is not { } channel)
            throw new InvalidOperationException("I can't access this channel.");

        var draft = new LfgDraft(r.GuildId, r.ChannelId, r.OwnerId)
        {
            ActivityName = r.ActivityName,
            PingRoleId = r.PingRoleId,
            Slots = r.Slots.Select(role => new Slot { Role = role }).ToList(),
            RequiredRoleIds = r.RequiredRoleIds?.ToList() ?? [],
            MinGs = r.MinGs,
            ProjectKey = r.ProjectKey
        };

        var group = new LfgGroup(draft)
        {
            StartsAt = r.StartsAt,
            ExpiresAt = time.ExpiresAt(r.StartsAt),
            // Напоминание — только если до старта больше времени, чем «за сколько напоминать»
            Reminded = !time.ReminderNeeded(r.StartsAt)
        };
        // Организатор участвует сам: слот 1 + последний использованный персонаж
        if (r.SeatOwner)
        {
            group.SeatOwner(characters.Get(r.GuildId, r.OwnerId, projects.Scope(r.GuildId, r.ProjectKey)).FirstOrDefault());
            signup.ApplyProfile(group, r.OwnerId); // класс и заметка персонажа организатора
        }

        var s = LfgRenderer.Group(group);
        string? content = null;
        var allowed = AllowedMentions.None;
        if (group.PingRoleId is { } roleId)
        {
            var when = group.StartsAt is { } t ? $" — <t:{t.ToUnixTimeSeconds()}:f>" : "";
            content = $"<@&{roleId}> — LFG for **{group.ActivityName}**{when}!";
            allowed = new AllowedMentions { RoleIds = [roleId] };
        }

        var message = await channel.SendMessageAsync(content, embed: s.Embed, components: s.Components, allowedMentions: allowed);
        group.MessageId = message.Id;
        store.Groups[group.Id] = group;
        store.SaveGroups();

        var jumpUrl = message.GetJumpUrl();
        var dms = r.NotifyByDm && group.PingRoleId is not null;
        if (dms) notifier.Start(group, jumpUrl);

        // Дубли поста: каналы, заданные в админке для проекта (или общие), + выбранные при создании
        var mirrors = projects.EffectiveAutoMirrors(r.GuildId, group.ProjectKey).Concat(r.MirrorChannelIds ?? Array.Empty<ulong>()).ToList();
        if (mirrors.Count > 0) await AddMirrorsAsync(group, mirrors);
        // Голосовая комната — только если при создании выбрали эту опцию
        if (r.CreateVoice && (client.GetUser(r.OwnerId) as IUser ?? await client.Rest.GetUserAsync(r.OwnerId)) is { } owner)
            await signup.CreateVoiceAsync(group, owner);
        return (group, jumpUrl, dms);
    }

    /// <summary>Сколько копий поста может быть у сбора (текстовые каналы и чаты голосовых).</summary>
    public const int MaxMirrors = 10;

    /// <summary>Копии поста сбора в других каналах (без пинга). Кнопки работают так же, как в основном посте.</summary>
    public async Task<int> AddMirrorsAsync(LfgGroup group, IEnumerable<ulong> channelIds)
    {
        var added = 0;
        await group.Gate.WaitAsync();
        try
        {
            foreach (var id in channelIds.Distinct().Where(id => id != group.ChannelId && group.Mirrors.All(m => m.ChannelId != id)).Take(MaxMirrors - group.Mirrors.Count))
            {
                if (await GetChannelAsync(id) is not { } channel) continue;
                try
                {
                    var s = LfgRenderer.Group(group);
                    var message = await channel.SendMessageAsync(embed: s.Embed, components: s.Components, allowedMentions: AllowedMentions.None);
                    group.Mirrors.Add(new LfgMirror { ChannelId = id, MessageId = message.Id });
                    added++;
                }
                catch (Discord.Net.HttpException)
                {
                    // нет доступа к каналу — пропускаем
                }
            }
            store.SaveGroups();
        }
        finally
        {
            group.Gate.Release();
        }
        return added;
    }

    private async Task<IMessageChannel?> GetChannelAsync(ulong channelId)
    {
        if (client.GetChannel(channelId) is IMessageChannel cached) return cached;
        try
        {
            return await client.Rest.GetChannelAsync(channelId) as IMessageChannel;
        }
        catch (Discord.Net.HttpException)
        {
            return null;
        }
    }
}
