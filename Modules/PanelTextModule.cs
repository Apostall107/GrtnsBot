using Discord;
using Discord.Interactions;
using LfgBot.Services;

namespace LfgBot.Modules;

/// <summary>
/// Тексты панелей из Discord — то же, что Panel texts на сайте: заголовок («-» — без заголовка) и текст с разметкой.
/// Подписи кнопок меняются на сайте.
/// </summary>
[RequireContext(ContextType.Guild)]
[RequireBotAdmin]
public sealed class PanelTextModule(PanelTextStore store, PanelService panels) : InteractionModuleBase<SocketInteractionContext>
{
    [SlashCommand("panel-text", "Edit the title and text of a panel (LFG, Tickets, Room controls)")]
    public async Task EditAsync(
        [Summary("panel", "Which panel"), Choice("LFG panel", "lfg"), Choice("Ticket panel", "tickets"), Choice("Room controls", "rooms")] string panel)
    {
        if (PanelDefs.Find(panel) is not { } def)
        {
            await RespondAsync("Unknown panel.", ephemeral: true);
            return;
        }
        var text = store.GetText(Context.Guild.Id, panel);
        var title = string.IsNullOrWhiteSpace(text?.Title) ? def.DefaultTitle : text!.Title == PanelDef.NoTitle ? "" : text.Title!;
        var modal = new ModalBuilder()
            .WithTitle($"Text — {def.Name}")
            .WithCustomId($"paneltext:save:{panel}")
            .AddTextInput("Title (plain text, empty = no title)", "title", TextInputStyle.Short,
                maxLength: PanelDef.MaxTitle, required: false, value: title)
            .AddTextInput("Text (Discord markdown, # headings work)", "text", TextInputStyle.Paragraph,
                maxLength: PanelDef.MaxDescription, required: false, value: def.Description(text))
            .Build();
        await RespondWithModalAsync(modal);
    }

    [ModalInteraction("paneltext:save:*")]
    public async Task SaveAsync(string panel, PanelTextModal modal)
    {
        if (PanelDefs.Find(panel) is not { } def)
        {
            await RespondAsync("Unknown panel.", ephemeral: true);
            return;
        }
        await DeferAsync(ephemeral: true);
        static string? Clean(string? value, string @default)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var t = value.Replace("\r\n", "\n").Trim();
            return t == @default ? null : t;
        }
        var current = store.GetText(Context.Guild.Id, panel);
        var title = string.IsNullOrWhiteSpace(modal.Title_) || modal.Title_.Trim() == PanelDef.NoTitle ? PanelDef.NoTitle : Clean(modal.Title_, def.DefaultTitle);
        var description = Clean(modal.Text, def.DefaultDescription);
        var labels = current?.Labels ?? new Dictionary<string, string>();
        store.SetText(Context.Guild.Id, panel, title is null && description is null && labels.Count == 0
            ? null
            : new PanelText { Title = title, Description = description, Labels = labels });
        var (updated, total) = await panels.RefreshAsync(Context.Guild.Id, panel);
        await FollowupAsync(total > 0
            ? $"Saved. Updated {updated} of {total} posted {def.Name}(s)."
            : "Saved. No posted panels yet — the next posted panel uses this text.", ephemeral: true);
    }
}

public sealed class PanelTextModal : IModal
{
    public string Title => "Panel text";

    [ModalTextInput("title")] public string? Title_ { get; set; }
    [ModalTextInput("text")] public string? Text { get; set; }
}
