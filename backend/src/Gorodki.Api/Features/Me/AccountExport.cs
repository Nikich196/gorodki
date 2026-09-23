using Gorodki.Api.Features.Captures;
using Gorodki.Api.Features.Config;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Features.Territory;
using Gorodki.Api.Infrastructure.Persistence;
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
public sealed record AccountExportResponse(
    long ExportedAtMs,
    ExportProfile Profile,
    IReadOnlyList<ExportSession> Sessions,
    IReadOnlyList<ExportRun> Runs,
    IReadOnlyList<ExportCapture> Captures,
    IReadOnlyList<ExportParcel> Land,
    IReadOnlyList<ExportFogTile> Fog,
    IReadOnlyList<PrivacyZoneResponse> PrivacyZones);

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
public sealed class AccountExport(AppDbContext db, GameConfigStore configs, TimeProvider time)
{
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

        var land = (await db.Parcels.AsNoTracking().Where(p => p.OwnerId == userId).OrderBy(p => p.Id).ToListAsync(cancellationToken))
            .Select(p => new ExportParcel(
                p.League,
                p.Level,
                p.LastVisitAt.ToUnixTimeMilliseconds(),
                TerritoryReader.LatLon(p.Geometry.ExteriorRing),
                [.. p.Geometry.InteriorRings.Select(TerritoryReader.LatLon)]))
            .ToList();

        var fog = await db.FogTiles.AsNoTracking()
            .Where(f => f.UserId == userId)
            .OrderBy(f => f.Layer).ThenBy(f => f.Season).ThenBy(f => f.TileX).ThenBy(f => f.TileY)
            .Select(f => new ExportFogTile(f.Layer, f.Season, f.TileX, f.TileY, f.CellCount, f.Bits))
            .ToListAsync(cancellationToken);

        var radius = (await configs.GetCurrentAsync(cancellationToken)).Rules.Privacy.ZoneRadiusMeters;
        var zones = (await db.PrivacyZones.AsNoTracking().Where(z => z.UserId == userId).OrderBy(z => z.CreatedAt).ToListAsync(cancellationToken))
            .Select(z => PrivacyZoneEndpoints.ToResponse(z, radius))
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
            zones);
    }
}
