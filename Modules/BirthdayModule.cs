using System.Globalization;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LfgBot.Services;

namespace LfgBot.Modules;

/// <summary>
/// /birthday — участник сам задаёт свой день рождения. В день рождения бот выдаёт роль (если включено в админке
/// и у человека есть роль «Участник») и снимает её на следующий день.
/// </summary>
[RequireContext(ContextType.Guild)]
public sealed class BirthdayModule(BirthdayStore store, BirthdayService service, AuditLog audit)
    : InteractionModuleBase<SocketInteractionContext>
{
    private ulong Gid => Context.Guild.Id;
    private string T(string key, params (string, object?)[] args) => Tx.Get(Gid, key, args);

    [SlashCommand("birthday", "Set your birthday to get the birthday role on your day")]
    public async Task BirthdayCommand()
    {
        var current = store.Of(Gid, Context.User.Id);
        var modal = new ModalBuilder()
            .WithTitle(T("birthday.title"))
            .WithCustomId("bday:set")
            .AddTextInput(T("birthday.label"), "date", TextInputStyle.Short,
                placeholder: "07.03", maxLength: 10, required: true,
                value: current is not null ? Format(current) : null)
            .Build();
        await RespondWithModalAsync(modal);
    }

    [ModalInteraction("bday:set")]
    public async Task OnSet(BirthdayModal modal)
    {
        if (!TryParse(modal.Date, out var bday))
        {
            await RespondAsync(T("birthday.badDate"), ephemeral: true);
            return;
        }
        if (!store.Set(Gid, Context.User.Id, bday))
        {
            await RespondAsync(T("bot.error"), ephemeral: true);
            return;
        }
        audit.FromDiscord(Gid, Context.User, "birthdays", "Set own birthday", Format(bday));
        var remove = new ComponentBuilder()
            .WithButton(T("birthday.remove"), "bday:remove", ButtonStyle.Secondary, new Emoji("🗑️"))
            .Build();
        await RespondAsync(T("birthday.saved", ("date", Format(bday))), components: remove, ephemeral: true);
        try { await service.ReconcileAsync(Gid); } catch { /* сверка не критична для ответа */ }
    }

    [ComponentInteraction("bday:remove")]
    public async Task OnRemove()
    {
        store.Remove(Gid, Context.User.Id);
        audit.FromDiscord(Gid, Context.User, "birthdays", "Removed own birthday", "");
        await RespondAsync(T("birthday.cleared"), ephemeral: true);
        try { await service.ReconcileAsync(Gid); } catch { /* не критично */ }
    }

    private static string Format(Birthday b) =>
        b.Year is { } y ? $"{b.Day:00}.{b.Month:00}.{y}" : $"{b.Day:00}.{b.Month:00}";

    /// <summary>Разбирает «ДД.ММ» или «ДД.ММ.ГГГГ» (точка, запятая, пробел, / или - как разделитель).</summary>
    public static bool TryParse(string? text, out Birthday bday)
    {
        bday = new Birthday();
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split(new[] { '.', ',', ' ', '/', '-' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var day)) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month)) return false;
        int? year = null;
        if (parts.Length == 3)
        {
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var y)) return false;
            year = y;
        }
        bday = new Birthday { Day = day, Month = month, Year = year };
        return bday.IsValid;
    }
}

public sealed class BirthdayModal : IModal
{
    public string Title => "Your birthday";

    [InputLabel("Date — DD.MM or DD.MM.YYYY")]
    [ModalTextInput("date")]
    public string? Date { get; set; }
}
