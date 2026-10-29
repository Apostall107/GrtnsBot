using System.Text;
using Discord;
using LfgBot.Models;

namespace LfgBot.Services;

public static class TicketRenderer
{
    public const string DefaultCategoryName = "Tickets";

    // ---------- Панель в канале ----------
    /// <summary>Тексты по умолчанию — <see cref="PanelDefs.Tickets"/>, свои — из веб-админки (PanelTextStore).</summary>
    public static (Embed Embed, MessageComponent Components) Panel(PanelText? text = null)
    {
        var def = PanelDefs.Tickets;
        var embed = new EmbedBuilder()
            .WithTitle(def.Title(text))
            .WithColor(Color.Orange)
            .WithDescription(def.Description(text))
            .Build();

        var comps = new ComponentBuilder()
            .WithButton(def.Label(text, "start"), "ticket:start", ButtonStyle.Primary, new Emoji("🎫"))
            .Build();

        return (embed, comps);
    }

    // ---------- Выбор проекта ----------
    public static MessageComponent ProjectPicker(IReadOnlyList<TicketProject> projects)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId("ticket:project")
            .WithPlaceholder("Choose a project");

        foreach (var p in projects.Take(TicketStore.MaxProjects))
            menu.AddOption(p.Name, p.Key, $"Apply to {p.Name}");

        return new ComponentBuilder().WithSelectMenu(menu).Build();
    }

    // ---------- Сообщение внутри тикета ----------
    public const int MaxCustomTextLength = 4000;

    /// <summary>
    /// Сообщение тикета: embed с системной информацией, под ним (если задан) embed со своим текстом проекта,
    /// ниже — кнопки. В тексте {applicant} → упоминание заявителя, {project} → название проекта.
    /// </summary>
    public static (Embed[] Embeds, MessageComponent Components) TicketMessage(Ticket t, TicketProject? p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Applicant: <@{t.UserId}>");
        sb.AppendLine($"Voice: <#{t.VoiceChannelId}>");
        if (p is { StaffRoleIds.Count: > 0 })
            sb.AppendLine($"Leadership: {string.Join(", ", p.StaffRoleIds.Select(r => $"<@&{r}>"))}");
        if (p is { MemberRoleIds.Count: > 0 })
            sb.AppendLine($"Roles on approve: {string.Join(", ", p.MemberRoleIds.Select(r => $"<@&{r}>"))}");
        sb.AppendLine();
        sb.AppendLine(t.ApprovedBy is { } by
            ? $"✅ **Approved** by <@{by}>"
            : "Leadership can **Approve** this application — the role is given and the ticket closes. " +
              "Anyone here can **Close** the ticket.");

        var embed = new EmbedBuilder()
            .WithTitle($"🎫 {p?.Name ?? "Project"} — application")
            .WithDescription(sb.ToString())
            .WithColor(t.ApprovedBy is null ? Color.Orange : Color.Green)
            .WithFooter($"Ticket {t.Id}")
            .WithTimestamp(t.CreatedAt)
            .Build();

        var embeds = new List<Embed> { embed };
        if (!string.IsNullOrWhiteSpace(p?.CustomText))
            embeds.Add(new EmbedBuilder()
                .WithDescription(ApplyPlaceholders(p.CustomText, t, p))
                .WithColor(Color.LightGrey)
                .Build());

        var comps = new ComponentBuilder()
            .WithButton("Approve", $"ticket:approve:{t.Id}", ButtonStyle.Success, new Emoji("✅"),
                disabled: t.ApprovedBy is not null)
            .WithButton("Close", $"ticket:close:{t.Id}", ButtonStyle.Danger, new Emoji("🔒"))
            .Build();

        return (embeds.ToArray(), comps);
    }

    private static string ApplyPlaceholders(string text, Ticket t, TicketProject p)
    {
        var result = text
            .Replace("{applicant}", $"<@{t.UserId}>", StringComparison.OrdinalIgnoreCase)
            .Replace("{project}", p.Name, StringComparison.OrdinalIgnoreCase);
        return result.Length <= 4096 ? result : result[..4096];
    }

    // ---------- Имена каналов ----------
    public static string TextChannelName(TicketProject p, IUser user) => Slug($"{p.Name}-{user.Username}");

    public static string VoiceChannelName(TicketProject p, IGuildUser user)
    {
        var name = $"🎙 {p.Name} • {user.DisplayName}";
        return name.Length <= 100 ? name : name[..100];
    }

    private static string Slug(string value)
    {
        var sb = new StringBuilder();
        foreach (var c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length == 0) slug = "ticket";
        return slug.Length <= 100 ? slug : slug[..100];
    }
}
