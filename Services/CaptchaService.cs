using System.Collections.Concurrent;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LfgBot.Services;

public enum CaptchaResult
{
    Passed,
    /// <summary>Неверный код, попытки ещё есть.</summary>
    Wrong,
    /// <summary>Время на картинку вышло (засчитано как неверная попытка), попытки ещё есть.</summary>
    TimedOut,
    /// <summary>Попытки кончились — пользователь заблокирован.</summary>
    Failed,
    NoChallenge
}

/// <summary>
/// Капча перед действиями с ботом для пользователей из списка супер-панели.
/// Состояние (задания, пройденные капчи, блокировки) — только в памяти: после перезапуска капчу проходят заново.
/// </summary>
public sealed class CaptchaService(CaptchaStore store, BotOwner owner, IServiceProvider services, ILogger<CaptchaService> logger)
{
    public const string FileName = "captcha.png";

    private sealed class Challenge(string code, int attemptsLeft)
    {
        public string Code { get; set; } = code;
        public int AttemptsLeft { get; set; } = attemptsLeft;
        /// <summary>До когда нужно ввести код текущей картинки. null — без лимита.</summary>
        public DateTimeOffset? Deadline { get; set; }
        public bool IsExpired => Deadline is { } d && DateTimeOffset.UtcNow > d;
    }

    private static DateTimeOffset? NewDeadline(CaptchaSettings settings) =>
        settings.TimeLimitSeconds > 0 ? DateTimeOffset.UtcNow.AddSeconds(settings.TimeLimitSeconds) : null;

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
        var gid = interaction.GuildId ?? 0;

        try
        {
            if (BlockedUntil(userId) is { } until)
            {
                await interaction.RespondAsync(
                    Tx.Get(gid, "captcha.blocked", ("time", $"<t:{until.ToUnixTimeSeconds()}:R>")), ephemeral: true);
                return true;
            }

            if (ConsumeVerification(userId)) return false;

            var settings = store.Get();
            string? notice = null;

            // Не успел ввести код прошлой картинки — это неверная попытка
            if (_challenges.TryGetValue(userId, out var previous) && previous.IsExpired)
            {
                previous.AttemptsLeft--;
                if (previous.AttemptsLeft <= 0)
                {
                    _challenges.TryRemove(userId, out _);
                    var blockedUntil = Block(userId, settings);
                    await interaction.RespondAsync(
                        Tx.Get(gid, "captcha.timeoutBlocked", ("time", $"<t:{blockedUntil.ToUnixTimeSeconds()}:R>")), ephemeral: true);
                    if (settings.TimeoutOnFail && interaction.User is SocketGuildUser member)
                        await services.GetRequiredService<CaptchaVoiceGuard>()
                            .PunishAsync(member, TimeSpan.FromMinutes(Math.Max(1, settings.BlockMinutes)));
                    return true;
                }

                previous.Deadline = NewDeadline(settings);
                notice = Tx.Get(gid, "captcha.previousTimedOut");
            }

            var challenge = _challenges.AddOrUpdate(userId,
                _ => new Challenge(CaptchaImage.NewCode(settings.CodeLength, settings.Mirror), settings.MaxAttempts)
                    { Deadline = NewDeadline(settings) },
                (_, existing) => { existing.Code = CaptchaImage.NewCode(settings.CodeLength, settings.Mirror); return existing; });

            var (embed, components) = BuildMessage(challenge, settings, notice, gid);
            using var image = new MemoryStream(Render(challenge.Code, settings));
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
    public (Embed Embed, MessageComponent Components, FileAttachment File)? Regenerate(ulong userId, string? notice = null, ulong gid = 0)
    {
        if (!_challenges.TryGetValue(userId, out var challenge)) return null;
        var settings = store.Get();
        challenge.Code = CaptchaImage.NewCode(settings.CodeLength, settings.Mirror);
        var (embed, components) = BuildMessage(challenge, settings, notice, gid);
        return (embed, components, new FileAttachment(new MemoryStream(Render(challenge.Code, settings)), FileName));
    }

    public static byte[] Render(string code, CaptchaSettings settings) =>
        CaptchaImage.RenderPng(code, settings.Mirror, settings.DistortionPercent);

    /// <summary>Подсказка, как читать картинку.</summary>
    public static string HowToRead(int length, bool mirror, ulong gid = 0)
    {
        var rows = length > CaptchaImage.PerRow ? Tx.Get(gid, "captcha.rows", ("rows", (length + CaptchaImage.PerRow - 1) / CaptchaImage.PerRow)) : "";
        return Tx.Get(gid, "captcha.length", ("length", length), ("rows", rows)) +
               (mirror ? Tx.Get(gid, "captcha.mirrored") : "");
    }

    /// <summary>Проверить ввод. input = null — время вышло без ввода (нажал Enter code слишком поздно).</summary>
    public CaptchaResult Submit(ulong userId, string? input, out int attemptsLeft)
    {
        attemptsLeft = 0;
        if (!_challenges.TryGetValue(userId, out var challenge)) return CaptchaResult.NoChallenge;

        var settings = store.Get();
        var timedOut = challenge.IsExpired;
        if (!timedOut && input is not null && CaptchaImage.Normalize(input) == challenge.Code)
        {
            _challenges.TryRemove(userId, out _);
            if (settings.ValidityMinutes <= 0) _oneShot[userId] = 1;
            else _verifiedUntil[userId] = DateTimeOffset.UtcNow.AddMinutes(settings.ValidityMinutes);
            return CaptchaResult.Passed;
        }

        challenge.AttemptsLeft--;
        attemptsLeft = challenge.AttemptsLeft;
        if (challenge.AttemptsLeft > 0)
        {
            challenge.Deadline = NewDeadline(settings); // следующая картинка — с новым временем
            return timedOut ? CaptchaResult.TimedOut : CaptchaResult.Wrong;
        }

        _challenges.TryRemove(userId, out _);
        Block(userId, settings);
        return CaptchaResult.Failed;
    }

    /// <summary>Истекло ли время на текущую картинку.</summary>
    public bool IsExpired(ulong userId) => _challenges.TryGetValue(userId, out var c) && c.IsExpired;

    private DateTimeOffset Block(ulong userId, CaptchaSettings settings)
    {
        var until = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, settings.BlockMinutes));
        _blockedUntil[userId] = until;
        return until;
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

    private static (Embed Embed, MessageComponent Components) BuildMessage(Challenge challenge, CaptchaSettings settings,
        string? notice, ulong gid)
    {
        var attemptsLeft = challenge.AttemptsLeft;
        var embed = new EmbedBuilder()
            .WithTitle(Tx.Get(gid, "captcha.title"))
            .WithColor(Tx.Color(gid, "color.captcha"))
            .WithDescription(
                (notice is null ? "" : notice + "\n\n") +
                Tx.Get(gid, "captcha.text", ("howto", HowToRead(challenge.Code.Length, settings.Mirror, gid)), ("attempts", attemptsLeft)) +
                (challenge.Deadline is { } d
                    ? "\n" + Tx.Get(gid, "captcha.timeLimit", ("time", $"<t:{d.ToUnixTimeSeconds()}:R>"))
                    : ""))
            .WithImageUrl($"attachment://{FileName}")
            .Build();

        var components = new ComponentBuilder()
            .WithButton(Tx.Get(gid, "captcha.enter"), "captcha:enter", ButtonStyle.Success, new Emoji("⌨️"))
            .WithButton(Tx.Get(gid, "captcha.new"), "captcha:new", ButtonStyle.Secondary, new Emoji("🔄"))
            .Build();

        return (embed, components);
    }
}
