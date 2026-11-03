using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LfgBot.Services;

namespace LfgBot.Modules;

/// <summary>Кнопки и выпадающие списки панелей ролей (панели настраиваются на сайте: Welcome &amp; roles).</summary>
public sealed class RolePanelModule(CommunityStore store, CommunityService community)
    : InteractionModuleBase<SocketInteractionContext>
{
    [ComponentInteraction("rp:b:*:*")]
    public async Task OnButton(string panelId, string roleIdText)
    {
        if (await ResolveAsync(panelId) is not { } x) return;
        if (!ulong.TryParse(roleIdText, out var roleId))
        {
            await RespondAsync(Tx.Get(Context.Guild.Id, "roles.gone"), ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        var text = await community.PickAsync(x.Panel, x.Member, roleId);
        await FollowupAsync(text, ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    [ComponentInteraction("rp:s:*")]
    public async Task OnSelect(string panelId, string[] values)
    {
        if (await ResolveAsync(panelId) is not { } x) return;
        await DeferAsync(ephemeral: true);
        var ids = values.Select(v => ulong.TryParse(v, out var id) ? id : 0).Where(id => id != 0).ToList();
        var text = await community.SelectAsync(x.Panel, x.Member, ids);
        await FollowupAsync(text, ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    private async Task<(Models.RolePanel Panel, SocketGuildUser Member)?> ResolveAsync(string panelId)
    {
        if (store.FindPanel(panelId) is not { } panel || Context.User is not SocketGuildUser member || member.Guild.Id != panel.GuildId)
        {
            await RespondAsync(Tx.Get(Context.Guild?.Id ?? 0, "roles.gone"), ephemeral: true);
            return null;
        }
        return (panel, member);
    }
}
