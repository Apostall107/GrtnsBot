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
            : "Leadership can **Approve** (the role is given and the ticket closes) or **Deny** (with a reason sent to the applicant). " +
              "Anyone here can **Close** the ticket.");

        var embed = new EmbedBuilder()
            .WithTitle($"🎫 {p?.Name ?? "Project"} — application")
            .WithDescription(sb.ToString())
            .WithColor(t.ApprovedBy is null ? Color.Orange : Color.Green)
            .WithFooter($"Ticket {t.Id}")
            .WithTimestamp(t.CreatedAt)
            .Build();

        var embeds = new List<Embed> { embed };
        if (t.Answers.Count > 0)
        {
            var form = new EmbedBuilder().WithTitle("📝 Application form").WithColor(Color.Blue);
            foreach (var a in t.Answers.Take(25))
                form.AddField(Clip(a.Question, 256), Clip(a.Answer, 1024));
            embeds.Add(form.Build());
        }
        if (!string.IsNullOrWhiteSpace(p?.CustomText))
            embeds.Add(new EmbedBuilder()
                .WithDescription(ApplyPlaceholders(p.CustomText, t, p))
                .WithColor(Color.LightGrey)
                .Build());

        var comps = new ComponentBuilder()
            .WithButton(PanelDefs.Label(t.GuildId, PanelDefs.TicketMessage, "approve"), $"ticket:approve:{t.Id}", ButtonStyle.Success, new Emoji("✅"),
                disabled: t.ApprovedBy is not null)
            .WithButton(PanelDefs.Label(t.GuildId, PanelDefs.TicketMessage, "deny"), $"ticket:deny:{t.Id}", ButtonStyle.Secondary, new Emoji("❌"),
                disabled: t.ApprovedBy is not null)
            .WithButton(PanelDefs.Label(t.GuildId, PanelDefs.TicketMessage, "close"), $"ticket:close:{t.Id}", ButtonStyle.Danger, new Emoji("🔒"))
            .Build();

        return (embeds.ToArray(), comps);
    }

    private static string Clip(string text, int max) =>
        string.IsNullOrWhiteSpace(text) ? "—" : text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>Окно анкеты проекта (вопросы → поля q0..q4).</summary>
    public static Modal FormModal(TicketProject p)
    {
        var title = $"Application — {p.Name}";
        var modal = new ModalBuilder()
            .WithTitle(title.Length <= 45 ? title : title[..45])
            .WithCustomId($"ticket:form:{p.Key}");
        for (var i = 0; i < Math.Min(p.Questions.Count, TicketQuestion.MaxQuestions); i++)
        {
            var q = p.Questions[i];
            modal.AddTextInput(q.Label, $"q{i}", q.Long ? TextInputStyle.Paragraph : TextInputStyle.Short,
                placeholder: q.Placeholder, maxLength: Math.Clamp(q.MaxLength, 1, 4000), required: q.Required);
        }
        return modal.Build();
    }

    /// <summary>Окно причины отказа.</summary>
    public static Modal DenyModal(string ticketId) => new ModalBuilder()
        .WithTitle("Deny the application")
        .WithCustomId($"ticket:denyok:{ticketId}")
        .AddTextInput("Reason (sent to the applicant)", "reason", TextInputStyle.Paragraph,
            placeholder: "Optional", maxLength: 500, required: false)
        .Build();

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
