using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Features.Scoring;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Runs;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Me;

/// <summary>
/// «Мои данные» (PLAN.md, §3.16, закон 99-З: «удаление ≤15 дней, выгрузка данных»; §3.10: «карта входит в „мои данные“»):
/// всё, что сервер хранит об игроке, одним JSON. Время — мс Unix, координаты — широта и долгота.
/// </summary>
/// <param name="ExportedAtMs">Когда выгружено.</param>
/// <param name="Profile">Профиль и согласия.</param>
/// <param name="Sessions">Входы (refresh-токены): когда выдан, до какого срока, отозван ли. Сами токены не хранятся.</param>
/// <param name="Runs">Забеги; точки и датчики — пока хранятся (14 дней), в том же виде, в каком их прислал телефон.</param>
/// <param name="Captures">Заявки петель и их итог.</param>
/// <param name="Land">Земля сейчас.</param>
/// <param name="Fog">Туман «Исследования» по слоям, сезонам и тайлам: биты открытых клеток, как в <c>GET /fog</c>.</param>
/// <param name="PrivacyZones">Приватные зоны.</param>
/// <param name="Rankings">Свои места в ежедневных срезах рейтингов (хранятся неделю).</param>
/// <param name="Scores">
/// Начисления очков сезона (§3.5) — те, что уже видны: очки за захват — с границы публичности его применения, как и
/// разбивка итога захвата (иначе бонусы выдали бы ещё скрытый чужой захват). Сервер присылает всегда; в контракте поле
/// необязательное — приложение, собранное раньше этого поля (и его тесты), разбирает выгрузку как прежде.
/// </param>
public sealed record AccountExportResponse(
    long ExportedAtMs,
    ExportProfile Profile,
    IReadOnlyList<ExportSession> Sessions,
    IReadOnlyList<ExportRun> Runs,
    IReadOnlyList<ExportCapture> Captures,
    IReadOnlyList<ExportParcel> Land,
    IReadOnlyList<ExportFogTile> Fog,
    IReadOnlyList<PrivacyZoneResponse> PrivacyZones,
    IReadOnlyList<ExportRanking> Rankings,
    IReadOnlyList<ExportScore>? Scores);

/// <param name="Season">Номер сезона; <c>null</c> — вне сезонов (предсезонье).</param>
/// <param name="Day">Игровые сутки по Минску, <c>yyyy-MM-dd</c>.</param>
/// <param name="Kind"><c>capture</c> — захват, <c>distance</c> — дистанция забега.</param>
/// <param name="CaptureId">Захват, за который начислено (у захвата).</param>
/// <param name="RunId">Забег (у захвата — его забег).</param>
public sealed record ExportScore(
    League League, int? Season, string Day, string Kind, int Points, Guid? CaptureId, Guid? RunId, long EffectiveAtMs);

/// <param name="Day">Игровые сутки среза, <c>yyyy-MM-dd</c>.</param>
/// <param name="Board">Рейтинг: <c>exploration</c> — «кто открыл больше».</param>
/// <param name="Layer"><c>foot</c>, <c>bike</c> или <c>total</c>.</param>
/// <param name="Season">Номер сезона; −1 — за всё время.</param>
/// <param name="Value">Значение: для «Исследования» — открытая площадь, м².</param>
public sealed record ExportRanking(string Day, string Board, string Layer, int Season, double Value, int Rank);

public sealed record ExportProfile(
    Guid Id,
    string DisplayName,
    short ColorIndex,
    string Role,
    bool PublicProfile,
    long CreatedAtMs,
    long? AgeConfirmedAtMs,
    int? ConsentVersion,
    long? ConsentedAtMs,
    string? InviteCode,
    long? DeletionRequestedAtMs);

public sealed record ExportSession(long CreatedAtMs, long ExpiresAtMs, long? RevokedAtMs);

/// <param name="PointsErasedAtMs">Когда стёрты точки (через 14 дней); тогда списки точек и датчиков пусты.</param>
public sealed record ExportRun(
    Guid Id,
    League League,
    RunSource Source,
    long StartedAtMs,
    long? EndedAtMs,
    RunStatus Status,
    Guid DeviceId,
    string AppVersion,
    bool MotionAuthorized,
    double? AcceptedMeters,
    int? FogNewCells,
    int? VisitedParcels,
    long? PointsErasedAtMs,
    IReadOnlyList<TrackPointDto> Points,
    IReadOnlyList<MotionSampleDto> Motion,
    IReadOnlyList<StepSampleDto> Steps);

public sealed record ExportCapture(
    Guid Id,
    Guid RunId,
    League League,
    CaptureStatus Status,
    string? RejectCode,
    long ReceivedAtMs,
    long? EffectiveAtMs,
    double? AreaSquareMeters,
    long? RolledBackAtMs);

/// <param name="Exterior">Внешнее кольцо: широта, долгота, широта, долгота…</param>
public sealed record ExportParcel(
    League League,
    short Level,
    long LastVisitAtMs,
    IReadOnlyList<double> Exterior,
    IReadOnlyList<IReadOnlyList<double>> Holes);

/// <param name="Season">Номер сезона; −1 — «за всё время».</param>
/// <param name="Bits">Сжатые биты открытых клеток тайла (как в <c>GET /fog</c>).</param>
public sealed record ExportFogTile(FogLayerKind Layer, int Season, int X, int Y, int CellCount, byte[] Bits);

/// <summary>Собирает выгрузку игрока.</summary>
public sealed class AccountExport(AppDbContext db, GameConfigStore configs, TerritoryReader territory, TimeProvider time)
{
    /// <summary>
    /// Земля — такой, какой её видит на карте сам игрок (публичная проекция, §3.16): чужой захват его земли виден ему через
    /// те же 20 минут, что и всем, и выгрузка не должна раскрывать его раньше. Тайлы — где у игрока земля сейчас и где её
    /// взяли ещё не публичные захваты.
    /// </summary>
    private async Task<List<ExportParcel>> OwnLandAsync(Guid userId, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMinutes((await configs.GetCurrentAsync(cancellationToken)).Rules.Privacy.PublicEventDelayMinutes);
        var horizon = TerritoryReader.PublicHorizon(time.GetUtcNow(), delay);
        var owned = await db.Parcels.AsNoTracking()
            .Where(p => p.OwnerId == userId)
            .Select(p => new { p.League, p.TileX, p.TileY })
            .Distinct()
            .ToListAsync(cancellationToken);
        var takenRecently = await db.CaptureJournalPieces.AsNoTracking()
            .Where(p => p.OwnerId == userId && !p.After)
            .Join(
                db.CaptureJournal.Where(j => j.AppliedAt > horizon),
                p => new { p.CaptureId, p.TileX, p.TileY },
                j => new { j.CaptureId, j.TileX, j.TileY },
                (p, j) => new { j.League, j.TileX, j.TileY })
            .Distinct()
            .ToListAsync(cancellationToken);

        var land = new List<ExportParcel>();
        foreach (var league in owned.Concat(takenRecently).GroupBy(t => t.League).OrderBy(g => g.Key))
        {
            var tiles = league.Select(t => (new TileKey(t.TileX, t.TileY), (long?)null)).Distinct().ToList();
            var seen = await territory.ReadAsync(league.Key, tiles, new TerritoryViewer(userId, Immediate: false), cancellationToken);
            land.AddRange(seen.Tiles
                .SelectMany(t => t.Parcels)
                .Where(p => p.OwnerId == userId)
                .Select(p => new ExportParcel(league.Key, p.Level, p.LastVisitAtMs, p.Exterior, p.Holes)));
        }

        return land;
    }

    public async Task<AccountExportResponse?> BuildAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var sessions = await db.RefreshTokens.AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderBy(t => t.CreatedAt)
            .Select(t => new ExportSession(
                t.CreatedAt.ToUnixTimeMilliseconds(),
                t.ExpiresAt.ToUnixTimeMilliseconds(),
                t.RevokedAt == null ? null : t.RevokedAt.Value.ToUnixTimeMilliseconds()))
            .ToListAsync(cancellationToken);

        var runs = new List<ExportRun>();
        foreach (var run in await db.Runs.AsNoTracking().Where(r => r.UserId == userId).OrderBy(r => r.StartedAt).ToListAsync(cancellationToken))
        {
            // Куски по одному забегу: выгрузка не держит в памяти все точки игрока сразу.
            var chunks = await db.RunChunks.AsNoTracking()
                .Where(c => c.RunId == run.Id && c.Points.Length > 0)
                .OrderBy(c => c.FirstSeq)
                .Select(c => c.Points)
                .ToListAsync(cancellationToken);
            var decoded = chunks.Select(bytes => TrackChunkCodec.Decode(bytes)).ToList();
            runs.Add(new ExportRun(
                run.Id,
                run.League,
                run.Source,
                run.StartedAt.ToUnixTimeMilliseconds(),
                run.EndedAt?.ToUnixTimeMilliseconds(),
                run.Status,
                run.DeviceId,
                run.AppVersion,
                run.MotionAuthorized,
                run.AcceptedMeters,
                run.FogNewCells,
                run.VisitedParcels,
                run.PointsPurgedAt?.ToUnixTimeMilliseconds(),
                [.. decoded.SelectMany(c => c.Points).Select(p => new TrackPointDto(
                    p.Seq, p.TimeMs, p.Latitude, p.Longitude, p.AccuracyMeters, p.SpeedMetersPerSecond, (int)p.Flags))],
                [.. decoded.SelectMany(c => c.Motion).Select(m => new MotionSampleDto(m.TimeMs, m.Activity))],
                [.. decoded.SelectMany(c => c.Steps).Select(s => new StepSampleDto(s.StartMs, s.EndMs, s.Steps))]));
        }

        var captures = (await db.Captures.AsNoTracking()
                .Where(c => c.UserId == userId)
                .OrderBy(c => c.ReceivedAt)
                .ToListAsync(cancellationToken))
            .Select(c => new ExportCapture(
                c.Id,
                c.RunId,
                c.League,
                c.Status,
                c.RejectCode,
                c.ReceivedAt.ToUnixTimeMilliseconds(),
                c.EffectiveAt?.ToUnixTimeMilliseconds(),
                c.AreaSquareMeters,
                c.RolledBackAt?.ToUnixTimeMilliseconds()))
            .ToList();

        var land = await OwnLandAsync(userId, cancellationToken);

        var fog = await db.FogTiles.AsNoTracking()
            .Where(f => f.UserId == userId)
            .OrderBy(f => f.Layer).ThenBy(f => f.Season).ThenBy(f => f.TileX).ThenBy(f => f.TileY)
            .Select(f => new ExportFogTile(f.Layer, f.Season, f.TileX, f.TileY, f.CellCount, f.Bits))
            .ToListAsync(cancellationToken);

        var radius = (await configs.GetCurrentAsync(cancellationToken)).Rules.Privacy.ZoneRadiusMeters;
        var zones = (await db.PrivacyZones.AsNoTracking().Where(z => z.UserId == userId).OrderBy(z => z.CreatedAt).ToListAsync(cancellationToken))
            .Select(z => PrivacyZoneEndpoints.ToResponse(z, radius))
            .ToList();

        var rankings = (await db.LeaderboardSnapshots.AsNoTracking()
                .Where(s => s.UserId == userId)
                .OrderBy(s => s.Day).ThenBy(s => s.Board).ThenBy(s => s.Layer).ThenBy(s => s.Season)
                .ToListAsync(cancellationToken))
            .Select(s => new ExportRanking(
                s.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                s.Board.ToString().ToLowerInvariant(),
                s.Layer.ToString().ToLowerInvariant(),
                s.Season,
                s.Value,
                s.Rank))
            .ToList();

        var scores = (await ScoreBook.Visible(db, time.GetUtcNow())
                .Where(e => e.UserId == userId)
                .OrderBy(e => e.EffectiveAt).ThenBy(e => e.Id)
                .ToListAsync(cancellationToken))
            .Select(e => new ExportScore(
                e.League,
                e.Season,
                e.GameDay.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                e.Kind.ToString().ToLowerInvariant(),
                e.Points,
                e.CaptureId,
                e.RunId,
                e.EffectiveAt.ToUnixTimeMilliseconds()))
            .ToList();

        return new AccountExportResponse(
            time.GetUtcNow().ToUnixTimeMilliseconds(),
            new ExportProfile(
                user.Id,
                user.DisplayName,
                user.ColorIndex,
                user.Role.ToString().ToLowerInvariant(),
                user.PublicProfile,
                user.CreatedAt.ToUnixTimeMilliseconds(),
                user.AgeConfirmedAt?.ToUnixTimeMilliseconds(),
                user.ConsentVersion,
                user.ConsentedAt?.ToUnixTimeMilliseconds(),
                user.InviteCode,
                user.DeletionRequestedAt?.ToUnixTimeMilliseconds()),
            sessions,
            runs,
            captures,
            land,
            fog,
            zones,
            rankings,
            scores);
    }
}
