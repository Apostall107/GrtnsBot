using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using LfgBot.Services;

namespace LfgBot.Modules;

/// <summary>
/// /super-panel — только для владельца бота (Bot:OwnerId). Сейчас: список пользователей,
/// которым бот показывает капчу перед любым действием, и настройки капчи.
/// </summary>
[RequireBotOwner]
public sealed class SuperPanelModule(CaptchaStore store, CaptchaService captcha, BotOwner owner)
    : InteractionModuleBase<SocketInteractionContext>
{
    private const int MaxOptions = 25;

    [SlashCommand("super-panel", "Owner-only super panel")]
    public async Task SuperPanelCommand()
    {
        var s = Screen(null);
        await RespondAsync(s.Content, embed: s.Embed, components: s.Components, ephemeral: true,
            allowedMentions: AllowedMentions.None);
    }

    [ComponentInteraction("sp:home")]
    public Task OnHome() => ShowAsync(Screen(null));

    // ---------- Список капчи ----------

    [ComponentInteraction("sp:cap:add")]
    public Task OnAdd(IUser[] users)
    {
        var skipped = users.Where(u => u.IsBot || owner.Is(u)).ToList();
        var ids = users.Except(skipped).Select(u => u.Id).ToList();
        var added = store.AddUsers(ids);
        foreach (var id in ids) captcha.Forget(id);

        var notice = added > 0 ? $"✅ Added {added} to the captcha list." : "⚠️ Nobody was added (already listed or the list is full).";
        if (skipped.Count > 0) notice += " Bots and the owner are never checked.";
        return ShowAsync(Screen(notice));
    }

    [ComponentInteraction("sp:cap:rm")]
    public Task OnRemove(string[] values)
    {
        var ids = values.Select(v => ulong.TryParse(v, out var id) ? id : 0).Where(id => id != 0).ToList();
        var removed = store.RemoveUsers(ids);
        foreach (var id in ids) captcha.Forget(id);
        return ShowAsync(Screen($"✅ Removed {removed} from the captcha list."));
    }

    // ---------- Настройки ----------

    [ComponentInteraction("sp:cap:settings")]
    public Task OnSettings()
    {
        var s = store.Get();
        var modal = new ModalBuilder()
            .WithTitle("Captcha settings")
            .WithCustomId("sp:cap:settingsset")
            .AddTextInput("Valid after solving, min (0 = every time)", "validity", TextInputStyle.Short,
                placeholder: "60", maxLength: 5, value: s.ValidityMinutes.ToString())
            .AddTextInput("Attempts per captcha (1–10)", "attempts", TextInputStyle.Short,
                placeholder: "3", maxLength: 2, value: s.MaxAttempts.ToString())
            .AddTextInput("Block after failing, min (1–1440)", "block", TextInputStyle.Short,
                placeholder: "10", maxLength: 4, value: s.BlockMinutes.ToString())
            .Build();
        return RespondWithModalAsync(modal);
    }

    [ModalInteraction("sp:cap:settingsset")]
    public Task OnSettingsSubmitted(SuperCaptchaSettingsModal modal)
    {
        if (!int.TryParse(modal.Validity?.Trim(), out var validity) || validity is < 0 or > 10080)
            return ShowAsync(Screen("⚠️ Validity must be a number from 0 to 10080 minutes."));
        if (!int.TryParse(modal.Attempts?.Trim(), out var attempts) || attempts is < 1 or > 10)
            return ShowAsync(Screen("⚠️ Attempts must be a number from 1 to 10."));
        if (!int.TryParse(modal.Block?.Trim(), out var block) || block is < 1 or > 1440)
            return ShowAsync(Screen("⚠️ Block time must be a number from 1 to 1440 minutes."));

        store.Update(s =>
        {
            s.ValidityMinutes = validity;
            s.MaxAttempts = attempts;
            s.BlockMinutes = block;
        });
        return ShowAsync(Screen("✅ Settings saved."));
    }

    [ComponentInteraction("sp:cap:kick")]
    public Task OnKickToggle()
    {
        var enabled = false;
        store.Update(s => enabled = s.KickOnFail = !s.KickOnFail);
        return ShowAsync(Screen(enabled
            ? "✅ Users who fail the captcha will also be kicked. The bot needs **Kick Members**."
            : "✅ Users who fail the captcha will only be blocked from the bot."));
    }

    [ComponentInteraction("sp:cap:reset")]
    public Task OnReset()
    {
        captcha.ResetAll();
        return ShowAsync(Screen("✅ Everyone on the list must solve the captcha again; all blocks were lifted."));
    }

    [ComponentInteraction("sp:cap:preview")]
    public async Task OnPreview()
    {
        var code = CaptchaImage.NewCode();
        using var image = new MemoryStream(CaptchaImage.RenderPng(code));
        var embed = new EmbedBuilder()
            .WithTitle("🔍 Captcha preview")
            .WithDescription($"This is what listed users see. Code: `{code}`")
            .WithImageUrl($"attachment://{CaptchaService.FileName}")
            .WithColor(Color.Orange)
            .Build();
        await RespondWithFileAsync(new FileAttachment(image, CaptchaService.FileName), embed: embed, ephemeral: true);
    }

    // ---------- Экран ----------

    private Services.Screen Screen(string? notice)
    {
        var s = store.Get();

        var sb = new StringBuilder();
        sb.AppendLine("**Captcha list** — these users must solve a picture captcha before using any bot command or button:");
        if (s.Users.Count == 0) sb.AppendLine("*empty*");
        foreach (var id in s.Users.Take(60))
        {
            var state = captcha.BlockedUntil(id) is { } b ? $" — ⛔ blocked until <t:{b.ToUnixTimeSeconds()}:t>"
                : captcha.VerifiedUntil(id) is { } v ? $" — ✅ verified until <t:{v.ToUnixTimeSeconds()}:t>"
                : "";
            sb.AppendLine($"• <@{id}>{state}");
        }
        if (s.Users.Count > 60) sb.AppendLine($"-# …and {s.Users.Count - 60} more");
        sb.AppendLine();
        sb.AppendLine("**Settings**");
        sb.AppendLine($"• Valid after solving: {(s.ValidityMinutes <= 0 ? "every command" : $"{s.ValidityMinutes} min")}");
        sb.AppendLine($"• Attempts per captcha: {s.MaxAttempts}");
        sb.AppendLine($"• On failure: blocked from the bot for {s.BlockMinutes} min{(s.KickOnFail ? " **and kicked from the server**" : "")}");
        sb.AppendLine();
        sb.AppendLine($"-# Owner <@{owner.Id}> always has full access to the bot and never gets a captcha.");

        var embed = new EmbedBuilder()
            .WithTitle("🦸 Super panel")
            .WithColor(Color.Purple)
            .WithDescription(sb.Length <= 4000 ? sb.ToString() : sb.ToString()[..4000])
            .Build();

        var add = new SelectMenuBuilder()
            .WithCustomId("sp:cap:add")
            .WithType(ComponentType.UserSelect)
            .WithPlaceholder("➕ Add users to the captcha list")
            .WithMinValues(1)
            .WithMaxValues(MaxOptions);

        var comps = new ComponentBuilder().WithSelectMenu(add, row: 0);

        if (s.Users.Count > 0)
        {
            var listed = s.Users.Take(MaxOptions).ToList();
            var remove = new SelectMenuBuilder()
                .WithCustomId("sp:cap:rm")
                .WithPlaceholder("➖ Remove users from the list")
                .WithMinValues(1)
                .WithMaxValues(listed.Count);
            foreach (var id in listed)
                remove.AddOption(Label(MemberName(id)), id.ToString(), id.ToString());
            comps.WithSelectMenu(remove, row: 1);
        }

        comps.WithButton("Settings", "sp:cap:settings", ButtonStyle.Primary, new Emoji("⚙️"), row: 2)
             .WithButton(s.KickOnFail ? "Kick on fail: ON" : "Kick on fail: OFF", "sp:cap:kick",
                 s.KickOnFail ? ButtonStyle.Danger : ButtonStyle.Secondary, new Emoji("👢"), row: 2)
             .WithButton("Reset verifications", "sp:cap:reset", ButtonStyle.Secondary, new Emoji("🔄"), row: 2)
             .WithButton("Preview captcha", "sp:cap:preview", ButtonStyle.Secondary, new Emoji("🔍"), row: 2)
             .WithButton("Refresh", "sp:home", ButtonStyle.Secondary, row: 3);

        return new Services.Screen(notice, embed, comps.Build());
    }

    private Task ShowAsync(Services.Screen s) => Context.Interaction switch
    {
        SocketMessageComponent c => c.UpdateAsync(m => Apply(m, s)),
        SocketModal modal => modal.UpdateAsync(m => Apply(m, s)),
        _ => RespondAsync(s.Content, embed: s.Embed, components: s.Components, ephemeral: true,
            allowedMentions: AllowedMentions.None)
    };

    private static void Apply(MessageProperties m, Services.Screen s)
    {
        m.Content = s.Content ?? "";
        m.Embeds = s.Embed is null ? Array.Empty<Embed>() : new[] { s.Embed };
        m.Components = s.Components;
        m.AllowedMentions = AllowedMentions.None;
    }

    private string MemberName(ulong id) =>
        Context.Guild?.GetUser(id)?.DisplayName ?? Context.Client.GetUser(id)?.Username ?? id.ToString();

    private static string Label(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return "—";
        return text.Length <= 100 ? text : text[..99] + "…";
    }
}

public sealed class SuperCaptchaSettingsModal : IModal
{
    public string Title => "Captcha settings";

    [InputLabel("Valid after solving, min (0 = every time)")]
    [ModalTextInput("validity")]
    public string? Validity { get; set; }

    [InputLabel("Attempts per captcha (1–10)")]
    [ModalTextInput("attempts")]
    public string? Attempts { get; set; }

    [InputLabel("Block after failing, min (1–1440)")]
    [ModalTextInput("block")]
    public string? Block { get; set; }
}
