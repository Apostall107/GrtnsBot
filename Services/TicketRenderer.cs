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
            .WithColor(def.Color(text))
            .WithDescription(def.Description(text))
            .Build();

        var comps = new ComponentBuilder()
            .WithButton(def.Label(text, "start"), "ticket:start", ButtonStyle.Primary, new Emoji("🎫"))
            .Build();

        return (embed, comps);
    }

    // ---------- Выбор проекта ----------
    public static MessageComponent ProjectPicker(ulong gid, IReadOnlyList<TicketProject> projects)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId("ticket:project")
            .WithPlaceholder(Tx.Get(gid, "ticket.pickPlaceholder"));

        foreach (var p in projects.Take(TicketStore.MaxProjects))
        {
            var desc = Tx.Clip(Tx.Get(gid, "ticket.pickOption", ("project", p.Name)), 100);
            menu.AddOption(p.Name, p.Key, desc.Length == 0 ? null : desc);
        }

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
        var gid = t.GuildId;
        var sb = new StringBuilder();
        sb.AppendLine(Tx.Get(gid, "ticket.msg.applicant", ("user", $"<@{t.UserId}>")));
        sb.AppendLine(Tx.Get(gid, "ticket.msg.voice", ("channel", $"<#{t.VoiceChannelId}>")));
        if (p is { StaffRoleIds.Count: > 0 })
            sb.AppendLine(Tx.Get(gid, "ticket.msg.leadership", ("roles", string.Join(", ", p.StaffRoleIds.Select(r => $"<@&{r}>")))));
        if (p is { MemberRoleIds.Count: > 0 })
            sb.AppendLine(Tx.Get(gid, "ticket.msg.roles", ("roles", string.Join(", ", p.MemberRoleIds.Select(r => $"<@&{r}>")))));
        var status = t.ApprovedBy is { } by
            ? Tx.Get(gid, "ticket.msg.approved", ("user", $"<@{by}>"))
            : Tx.Get(gid, "ticket.msg.howTo");
        if (status.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine(status);
        }

        var embed = new EmbedBuilder()
            .WithTitle(Tx.Get(gid, "ticket.msg.title", ("project", p?.Name ?? "Project")))
            .WithDescription(sb.ToString())
            .WithColor(Tx.Color(gid, t.ApprovedBy is null ? "color.ticket.open" : "color.ticket.approved"))
            .WithFooter(Tx.Get(gid, "ticket.msg.footer", ("id", t.Id)))
            .WithTimestamp(t.CreatedAt)
            .Build();

        var embeds = new List<Embed> { embed };
        if (t.Answers.Count > 0)
        {
            var form = new EmbedBuilder().WithTitle(Tx.Get(gid, "ticket.msg.formTitle")).WithColor(Tx.Color(gid, "color.ticket.form"));
            foreach (var a in t.Answers.Take(25))
                form.AddField(Clip(a.Question, 256), Clip(a.Answer, 1024));
            embeds.Add(form.Build());
        }
        if (!string.IsNullOrWhiteSpace(p?.CustomText))
            embeds.Add(new EmbedBuilder()
                .WithDescription(ApplyPlaceholders(p.CustomText, t, p))
                .WithColor(Tx.Color(gid, "color.ticket.custom"))
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
    public static Modal FormModal(ulong gid, TicketProject p)
    {
        var title = Tx.Get(gid, "ticket.formTitle", ("project", p.Name));
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
    public static Modal DenyModal(ulong gid, string ticketId) => new ModalBuilder()
        .WithTitle(Tx.Get(gid, "ticket.deny.title"))
        .WithCustomId($"ticket:denyok:{ticketId}")
        .AddTextInput(Tx.Get(gid, "ticket.deny.label"), "reason", TextInputStyle.Paragraph,
            placeholder: Tx.Get(gid, "ticket.deny.hint"), maxLength: 500, required: false)
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
        var name = Tx.Get(user.GuildId, "ticket.voiceName", ("project", p.Name), ("user", user.DisplayName));
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
