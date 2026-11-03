using Discord;
using Discord.Interactions;
using LfgBot.Services;

namespace LfgBot.Modules;

/// <summary>Кнопка «Обновить GS» из напоминания (в канале или в ЛС): выбрать персонажа → окно с GS.</summary>
public sealed class GsReminderModule(GsReminderService reminder, CharacterStore characters, LfgProfileService profiles)
    : InteractionModuleBase<SocketInteractionContext>
{
    [ComponentInteraction("gsr:open:*")]
    public async Task OnOpen(string guildIdText)
    {
        if (!ulong.TryParse(guildIdText, out var guildId)) return;
        var choices = reminder.Choices(guildId, Context.User.Id);
        if (choices.Count == 0)
        {
            await RespondAsync(Tx.Get(guildId, "gs.reminder.noCharacters"), ephemeral: true);
            return;
        }
        var menu = new SelectMenuBuilder().WithCustomId($"gsr:pick:{guildId}").WithPlaceholder(Tx.Get(guildId, "gs.reminder.pick"));
        foreach (var (value, label, description) in choices)
            menu.AddOption(label, value.Length <= 100 ? value : value[..100], description);
        await RespondAsync(Tx.Get(guildId, "gs.reminder.which"), components: new ComponentBuilder().WithSelectMenu(menu).Build(), ephemeral: true);
    }

    [ComponentInteraction("gsr:pick:*")]
    public async Task OnPick(string guildIdText, string[] values)
    {
        if (!ulong.TryParse(guildIdText, out var guildId) || values.Length == 0) return;
        var (scope, name) = Split(values[0]);
        var gs = characters.GetInfo(guildId, Context.User.Id, name, scope)?.Gs;
        var modal = new ModalBuilder()
            .WithTitle(Tx.Clip(Tx.Get(guildId, "gs.reminder.modalTitle", ("name", name)), 45))
            .WithCustomId($"gsr:m:{guildId}|{values[0]}")
            .AddTextInput(Tx.Get(guildId, "lfg.profile.gsLabel"), "gs", TextInputStyle.Short, placeholder: Tx.Get(guildId, "lfg.profile.gsHint"),
                maxLength: 6, required: true, value: gs?.ToString())
            .Build();
        await RespondWithModalAsync(modal);
    }

    [ModalInteraction("gsr:m:*")]
    public async Task OnSubmit(string rest, GsModal modal)
    {
        var bar = rest.IndexOf('|');
        if (bar < 0 || !ulong.TryParse(rest[..bar], out var guildId)) return;
        var (scope, name) = Split(rest[(bar + 1)..]);
        if (!int.TryParse((modal.Gs ?? "").Trim(), out var gs) || gs is < 0 or > 99999)
        {
            await RespondAsync(Tx.Get(guildId, "lfg.profile.badGs"), ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        if (!characters.SetGs(guildId, Context.User.Id, name, gs, scope, confirm: true))
        {
            await FollowupAsync(Tx.Get(guildId, "char.gsArmory", ("name", Format.Sanitize(name))), ephemeral: true);
            return;
        }
        var updated = await profiles.RefreshCharacterAsync(guildId, Context.User.Id, name, scope);
        await FollowupAsync(Tx.Get(guildId, "gs.reminder.saved", ("name", Format.Sanitize(name)), ("gs", gs), ("groups", updated)), ephemeral: true);
    }

    /// <summary>"scope|имя" → (scope или null, имя).</summary>
    private static (string? Scope, string Name) Split(string value)
    {
        var bar = value.IndexOf('|');
        var scope = bar <= 0 ? null : value[..bar];
        return (scope, bar < 0 ? value : value[(bar + 1)..]);
    }
}

public sealed class GsModal : IModal
{
    public string Title => "GS";

    [ModalTextInput("gs")]
    public string? Gs { get; set; }
}
