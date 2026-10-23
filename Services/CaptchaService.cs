using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

public enum CaptchaResult { Passed, Wrong, Failed, NoChallenge }

/// <summary>
/// Капча перед действиями с ботом для пользователей из списка супер-панели.
/// Состояние (задания, пройденные капчи, блокировки) — только в памяти: после перезапуска капчу проходят заново.
/// </summary>
public sealed class CaptchaService(CaptchaStore store, BotOwner owner, ILogger<CaptchaService> logger)
{
    public const string FileName = "captcha.png";

    private sealed class Challenge(string code, int attemptsLeft)
    {
        public string Code { get; set; } = code;
        public int AttemptsLeft { get; set; } = attemptsLeft;
    }

    private readonly ConcurrentDictionary<ulong, Challenge> _challenges = new();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _verifiedUntil = new();
    private readonly ConcurrentDictionary<ulong, byte> _oneShot = new();
    private readonly ConcurrentDictionary<ulong, DateTimeOffset> _blockedUntil = new();

    /// <summary>
    /// Вызывается до выполнения любой команды. true — взаимодействие перехвачено (показана капча
    /// или сообщение о блокировке), команду выполнять не нужно.
    /// </summary>
    public async Task<bool> InterceptAsync(SocketInteraction interaction)
    {
        if (interaction is SocketAutocompleteInteraction) return false;
        if (IsCaptchaInteraction(interaction)) return false;

        var userId = interaction.User.Id;
        if (owner.Is(userId) || !store.IsListed(userId)) return false;

        try
        {
            if (BlockedUntil(userId) is { } until)
            {
                await interaction.RespondAsync(
                    $"⛔ You failed the verification. You can use the bot again <t:{until.ToUnixTimeSeconds()}:R>.",
                    ephemeral: true);
                return true;
            }

            if (ConsumeVerification(userId)) return false;

            var settings = store.Get();
            var challenge = _challenges.AddOrUpdate(userId,
                _ => new Challenge(CaptchaImage.NewCode(), settings.MaxAttempts),
                (_, existing) => { existing.Code = CaptchaImage.NewCode(); return existing; });

            var (embed, components) = BuildMessage(challenge.AttemptsLeft, null);
            using var image = new MemoryStream(CaptchaImage.RenderPng(challenge.Code));
            await interaction.RespondWithFileAsync(new FileAttachment(image, FileName),
                embed: embed, components: components, ephemeral: true);
            return true;
        }
        catch (Exception ex)
        {
            // Не смогли показать капчу — не пропускаем: лучше ошибка, чем обход проверки
            logger.LogWarning(ex, "Failed to show captcha to {UserId}", userId);
            return true;
        }
    }

    public bool HasChallenge(ulong userId) => _challenges.ContainsKey(userId);

    /// <summary>Новая картинка с новым кодом, попытки сохраняются. null — задания нет.</summary>
    public (Embed Embed, MessageComponent Components, FileAttachment File)? Regenerate(ulong userId, string? notice = null)
    {
        if (!_challenges.TryGetValue(userId, out var challenge)) return null;
        challenge.Code = CaptchaImage.NewCode();
        var (embed, components) = BuildMessage(challenge.AttemptsLeft, notice);
        return (embed, components, new FileAttachment(new MemoryStream(CaptchaImage.RenderPng(challenge.Code)), FileName));
    }

    public CaptchaResult Submit(ulong userId, string? input, out int attemptsLeft)
    {
        attemptsLeft = 0;
        if (!_challenges.TryGetValue(userId, out var challenge)) return CaptchaResult.NoChallenge;

        var settings = store.Get();
        if (string.Equals((input ?? "").Trim(), challenge.Code, StringComparison.OrdinalIgnoreCase))
        {
            _challenges.TryRemove(userId, out _);
            if (settings.ValidityMinutes <= 0) _oneShot[userId] = 1;
            else _verifiedUntil[userId] = DateTimeOffset.UtcNow.AddMinutes(settings.ValidityMinutes);
            return CaptchaResult.Passed;
        }

        challenge.AttemptsLeft--;
        attemptsLeft = challenge.AttemptsLeft;
        if (challenge.AttemptsLeft > 0) return CaptchaResult.Wrong;

        _challenges.TryRemove(userId, out _);
        _blockedUntil[userId] = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, settings.BlockMinutes));
        return CaptchaResult.Failed;
    }

    /// <summary>Сбросить состояние пользователя (при удалении из списка).</summary>
    public void Forget(ulong userId)
    {
        _challenges.TryRemove(userId, out _);
        _verifiedUntil.TryRemove(userId, out _);
        _oneShot.TryRemove(userId, out _);
        _blockedUntil.TryRemove(userId, out _);
    }

    /// <summary>Все снова проходят капчу, блокировки снимаются.</summary>
    public void ResetAll()
    {
        _challenges.Clear();
        _verifiedUntil.Clear();
        _oneShot.Clear();
        _blockedUntil.Clear();
    }

    public DateTimeOffset? BlockedUntil(ulong userId)
    {
        if (!_blockedUntil.TryGetValue(userId, out var until)) return null;
        if (until > DateTimeOffset.UtcNow) return until;
        _blockedUntil.TryRemove(userId, out _);
        return null;
    }

    /// <summary>Заблокирован и включено наказание на сервере — отключать от голоса.</summary>
    public bool IsVoiceRestricted(ulong userId) => BlockedUntil(userId) is not null && store.Get().TimeoutOnFail;

    public DateTimeOffset? VerifiedUntil(ulong userId) =>
        _verifiedUntil.TryGetValue(userId, out var until) && until > DateTimeOffset.UtcNow ? until : null;

    private bool ConsumeVerification(ulong userId)
    {
        if (_oneShot.TryRemove(userId, out _)) return true;
        return VerifiedUntil(userId) is not null;
    }

    private static bool IsCaptchaInteraction(SocketInteraction interaction) => interaction switch
    {
        SocketMessageComponent c => c.Data.CustomId.StartsWith("captcha:", StringComparison.Ordinal),
        SocketModal m => m.Data.CustomId.StartsWith("captcha:", StringComparison.Ordinal),
        _ => false
    };

    private static (Embed Embed, MessageComponent Components) BuildMessage(int attemptsLeft, string? notice)
    {
        var embed = new EmbedBuilder()
            .WithTitle("🤖 Verification required")
            .WithColor(Color.Orange)
            .WithDescription(
                (notice is null ? "" : notice + "\n\n") +
                "Before using the bot, type the code from the picture.\n" +
                "Click **Enter code**. Letters are not case-sensitive. Can't read it? Click **New picture**.\n\n" +
                $"Attempts left: **{attemptsLeft}**")
            .WithImageUrl($"attachment://{FileName}")
            .Build();

        var components = new ComponentBuilder()
            .WithButton("Enter code", "captcha:enter", ButtonStyle.Success, new Emoji("⌨️"))
            .WithButton("New picture", "captcha:new", ButtonStyle.Secondary, new Emoji("🔄"))
            .Build();

        return (embed, components);
    }
}
