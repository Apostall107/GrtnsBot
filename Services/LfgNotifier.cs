using Discord;
using Discord.WebSocket;
using LfgBot.Models;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

/// <summary>Рассылает ЛС участникам с ролью активности о новом сборе. Работает в фоне, с паузой между сообщениями.</summary>
public sealed class LfgNotifier(DiscordSocketClient client, LfgNotifyStore prefs, ILogger<LfgNotifier> logger)
{
    // Пауза между ЛС, чтобы Discord не посчитал рассылку спамом
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    /// <summary>Discord: «You are opening direct messages too fast».</summary>
    private const int OpeningDmsTooFast = 40003;

    /// <summary>После ошибки 40003 рассылки ставятся на паузу, чтобы не довести бота до спам-флага.</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);

    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;

    public void Start(LfgGroup group, string jumpUrl) => _ = Task.Run(() => SendAsync(group, jumpUrl));

    private async Task SendAsync(LfgGroup group, string jumpUrl)
    {
        try
        {
            if (DateTimeOffset.UtcNow < _pausedUntil)
            {
                logger.LogWarning("LFG {GroupId}: DMs skipped — paused until {Until} after Discord's 'too fast' error",
                    group.Id, _pausedUntil);
                return;
            }

            var guild = client.GetGuild(group.GuildId);
            if (guild is null || group.PingRoleId is not { } roleId || guild.GetRole(roleId) is not { } role) return;

            if (!guild.HasAllMembers) await guild.DownloadUsersAsync();

            var targets = role.Members
                .Where(m => !m.IsBot && m.Id != group.OwnerId && prefs.IsEnabled(guild.Id, m.Id))
                .ToList();

            var embed = LfgRenderer.DmNotice(group, guild.Name);
            // Запись прямо из ЛС: кнопки ролей (серая = мест нет, запишет в резерв) + ссылка на пост
            var builder = new ComponentBuilder();
            foreach (var slotRole in SlotRoleExtensions.All)
                builder.WithButton(slotRole.Label(), $"lfgdm:join:{group.Id}:{slotRole}",
                    group.CanJoinAs(slotRole) ? ButtonStyle.Primary : ButtonStyle.Secondary,
                    new Emoji(slotRole.Emoji()), row: 0);
            builder.WithButton("Open the group", style: ButtonStyle.Link, url: jumpUrl, row: 1);
            builder.WithButton("Turn off these DMs", $"lfgdm:off:{guild.Id}", ButtonStyle.Secondary, new Emoji("🔕"), row: 1);
            var buttons = builder.Build();

            int sent = 0, failed = 0;
            foreach (var member in targets)
            {
                try
                {
                    await member.SendMessageAsync(embed: embed, components: buttons);
                    sent++;
                }
                catch (Discord.Net.HttpException ex) when ((int?)ex.DiscordCode == OpeningDmsTooFast)
                {
                    // Автостоп: Discord считает, что бот открывает ЛС слишком быстро
                    _pausedUntil = DateTimeOffset.UtcNow + Cooldown;
                    logger.LogWarning("LFG {GroupId}: Discord says DMs are opened too fast — stopped after {Sent} of {Total}, " +
                                      "DMs paused for {Minutes} min", group.Id, sent, targets.Count, (int)Cooldown.TotalMinutes);
                    break;
                }
                catch (Discord.Net.HttpException)
                {
                    failed++; // закрытые ЛС (50007) — пропускаем
                }

                await Task.Delay(Delay);
            }

            logger.LogInformation("LFG {GroupId} ({Activity}): DMs sent {Sent}, failed {Failed}",
                group.Id, group.ActivityName, sent, failed);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "LFG DM notification failed for {GroupId}", group.Id);
        }
    }
}
