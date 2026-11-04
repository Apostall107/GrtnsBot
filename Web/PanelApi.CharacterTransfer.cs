using System.Text;
using System.Text.Json;
using Discord.WebSocket;
using LfgBot.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace LfgBot.Web;

/// <summary>
/// Только владельцу бота: выгрузка всех персонажей сервера (JSON — для переноса на другой инстанс, CSV — для таблиц)
/// и загрузка такого JSON обратно.
/// </summary>
public static partial class PanelApi
{
    public sealed record CharacterExportArmory(string CharacterId, int ServerId, string? ServerName, string? Name, string? ClassName,
        int Level, int? CombatPower, int? ItemLevel, string? Legion, DateTimeOffset? SyncedAt);

    public sealed record CharacterExportRow(string UserId, string? UserName, string? Project, string? ProjectName, string Name,
        string? ClassRoleId, string? ClassName, int? Gs, DateTimeOffset? GsUpdatedAt, int? Race, int? ServerId, string? Server,
        string? Comment, CharacterExportArmory? Armory);

    public sealed record CharacterExportFile(int Version, string GuildId, string? GuildName, DateTimeOffset ExportedAt,
        List<CharacterExportRow> Characters);

    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static void MapCharacterTransfer(RouteGroupBuilder g)
    {
        g.MapGet("/", async (ulong guildId, string? format, DiscordSocketClient client, [FromServices] CharacterStore characters,
            [FromServices] LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            await WarmMembersAsync(client, guild, characters.All(guildId).Select(c => c.UserId));
            var def = projects.DefaultKey(guildId);
            var rows = characters.All(guildId).Select(c =>
            {
                var i = c.Info;
                var projectKey = c.Scope ?? def;
                var a = i?.Armory;
                var user = guild.GetUser(c.UserId);
                return new CharacterExportRow(c.UserId.ToString(), user?.Username, projectKey, projects.Find(guildId, projectKey)?.Name, c.Name,
                    i?.ClassRoleId?.ToString(), i?.ClassRoleId is { } rid ? guild.GetRole(rid)?.Name : null,
                    i?.Gs, i?.GsUpdatedAt, i?.Race, i?.ServerId, a?.ServerName ?? OfficialArmory.ServerName(i?.ServerId), i?.Comment,
                    a is null || a.Error is not null ? null
                        : new CharacterExportArmory(a.CharacterId, a.ServerId, a.ServerName, a.Name, a.ClassName, a.Level, a.CombatPower, a.ItemLevel, a.Legion, a.SyncedAt));
            }).OrderBy(r => r.UserName ?? r.UserId).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmm");
            if (string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase))
            {
                static string Q(object? v) => v is null ? "" : $"\"{v.ToString()!.Replace("\"", "\"\"")}\"";
                var sb = new StringBuilder("﻿user_id,user,project,character,class,gs,server,cp,legion,armory_id,comment\r\n");
                foreach (var r in rows)
                    sb.AppendJoin(',', Q(r.UserId), Q(r.UserName), Q(r.ProjectName ?? r.Project), Q(r.Name), Q(r.ClassName), Q(r.Gs),
                        Q(r.Server), Q(r.Armory?.CombatPower), Q(r.Armory?.Legion), Q(r.Armory?.CharacterId), Q(r.Comment)).Append("\r\n");
                return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv; charset=utf-8", $"characters-{stamp}.csv");
            }
            var file = new CharacterExportFile(1, guild.Id.ToString(), guild.Name, DateTimeOffset.UtcNow, rows);
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(file, ExportJson), "application/json", $"characters-{stamp}.json");
        });

        // Загрузка JSON, выгруженного выше (можно с другого инстанса / сервера). Класс ищется по ID роли, затем по имени;
        // проект — по ключу (нет такого проекта — в проект по умолчанию). Существующие профили не трогаются без overwrite.
        g.MapPost("/import", async (ulong guildId, bool? overwrite, HttpRequest request, DiscordSocketClient client,
            [FromServices] CharacterStore characters, [FromServices] LfgProjectStore projects) =>
        {
            if (client.GetGuild(guildId) is not { } guild) return GuildNotFound();
            CharacterExportFile? file;
            try { file = await JsonSerializer.DeserializeAsync<CharacterExportFile>(request.Body, ExportJson); }
            catch (JsonException) { return Error("This is not a characters export file (.json)."); }
            if (file?.Characters is not { Count: > 0 }) return Error("The file has no characters.");

            int added = 0, updated = 0, skipped = 0, full = 0, bad = 0;
            foreach (var r in file.Characters)
            {
                if (!ulong.TryParse(r.UserId, out var userId) || string.IsNullOrWhiteSpace(r.Name)) { bad++; continue; }
                ulong? classId = ulong.TryParse(r.ClassRoleId, out var cid) && guild.GetRole(cid) is not null ? cid
                    : r.ClassName is { Length: > 0 } cn ? guild.Roles.FirstOrDefault(x => x.Name.Equals(cn, StringComparison.OrdinalIgnoreCase))?.Id : null;
                var a = r.Armory;
                var info = new CharacterInfo
                {
                    ClassRoleId = classId,
                    Comment = r.Comment is { Length: > CharacterStore.MaxCommentLength } c ? c[..CharacterStore.MaxCommentLength] : r.Comment,
                    Gs = r.Gs, GsUpdatedAt = r.GsUpdatedAt, Race = r.Race, ServerId = r.ServerId,
                    Armory = a is null ? null : new ArmoryLink
                    {
                        CharacterId = a.CharacterId, ServerId = a.ServerId, ServerName = a.ServerName, Name = a.Name, ClassName = a.ClassName,
                        Level = a.Level, CombatPower = a.CombatPower, ItemLevel = a.ItemLevel, Legion = a.Legion, SyncedAt = a.SyncedAt
                    }
                };
                switch (characters.Import(guildId, userId, projects.Scope(guildId, r.Project), r.Name, info, overwrite == true))
                {
                    case "added": added++; break;
                    case "updated": updated++; break;
                    case "full": full++; break;
                    default: skipped++; break;
                }
            }
            return Results.Ok(new { total = file.Characters.Count, added, updated, skipped, full, bad });
        });
    }
}
