using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LfgBot.Services;

namespace LfgBot.Modules;

/// <summary>Кнопки и выпадающие списки панелей ролей (панели настраиваются на сайте: Role panels).</summary>
public sealed class RolePanelModule(CommunityStore store, CommunityService community)
    : InteractionModuleBase<SocketInteractionContext>
{
    [ComponentInteraction("rp:b:*:*")]
    public async Task OnButton(string panelId, string bindingId)
    {
        if (await ResolveAsync(panelId) is not { } x) return;
        await DeferAsync(ephemeral: true);
        var text = await community.PickAsync(x.Panel, x.Member, bindingId);
        var view = await community.PersonalViewAsync(x.Panel, x.Member.Id, text);
        await FollowupAsync(view.Text, components: view.Components, ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    /// <summary>«Мои роли»: показать личный вид без изменений.</summary>
    [ComponentInteraction("rp:my:*")]
    public async Task OnMyRoles(string panelId)
    {
        if (await ResolveAsync(panelId) is not { } x) return;
        await DeferAsync(ephemeral: true);
        var view = await community.PersonalViewAsync(x.Panel, x.Member.Id, null);
        await FollowupAsync(view.Text, components: view.Components, ephemeral: true, allowedMentions: AllowedMentions.None);
    }

    /// <summary>Кнопки личного вида: переключают роль и обновляют это же (эфемерное) сообщение.</summary>
    [ComponentInteraction("rp:mb:*:*")]
    public async Task OnPersonalButton(string panelId, string bindingId)
    {
        if (await ResolveAsync(panelId) is not { } x) return;
        await DeferAsync();
        var text = await community.PickAsync(x.Panel, x.Member, bindingId);
        var view = await community.PersonalViewAsync(x.Panel, x.Member.Id, text);
        await ModifyOriginalResponseAsync(m =>
        {
            m.Content = view.Text;
            m.Components = view.Components;
            m.AllowedMentions = AllowedMentions.None;
        });
    }

    [ComponentInteraction("rp:s:*")]
    public async Task OnSelect(string panelId, string[] values)
    {
        if (await ResolveAsync(panelId) is not { } x) return;
        await DeferAsync(ephemeral: true);
        var text = await community.SelectAsync(x.Panel, x.Member, values);
        var view = await community.PersonalViewAsync(x.Panel, x.Member.Id, text);
        await FollowupAsync(view.Text, components: view.Components, ephemeral: true, allowedMentions: AllowedMentions.None);
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
