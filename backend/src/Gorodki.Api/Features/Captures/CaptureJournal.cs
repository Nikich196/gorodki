using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Territory;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Gorodki.Api.Features.Captures;

/// <summary>
/// Журнал захватов (PLAN.md, §7.3, шаг B.5): что захват изменил по тайлам — след и земля в нём до и после, геометрия
/// в TWKB. Пишется в транзакции захвата, хранится <see cref="CaptureProcessor.JournalRetention"/>, нужен для отката.
/// </summary>
public static class CaptureJournal
{
    /// <summary>Добавляет записи журнала захвата в контекст (сохраняются вместе с землёй).</summary>
    /// <exception cref="TerritoryEngineException">Геометрия не на сетке — ошибка движка.</exception>
    public static void Add(AppDbContext db, Guid captureId, League league, DateTimeOffset appliedAt, IReadOnlyList<TileChange> changes)
    {
        foreach (var change in changes)
        {
            db.CaptureJournal.Add(new CaptureJournalEntity
            {
                CaptureId = captureId,
                TileX = change.Tile.X,
                TileY = change.Tile.Y,
                League = league,
                AppliedAt = appliedAt,
                Footprint = Encode(change.Footprint),
            });
            db.CaptureJournalPieces.AddRange(change.Before.Select(p => ToEntity(captureId, change.Tile, after: false, p)));
            db.CaptureJournalPieces.AddRange(change.After.Select(p => ToEntity(captureId, change.Tile, after: true, p)));
        }
    }

    /// <summary>Журнал захвата по тайлам (пусто — захват ничего не изменил или журнал уже стёрт).</summary>
    public static async Task<IReadOnlyList<TileChange>> LoadAsync(AppDbContext db, Guid captureId, CancellationToken cancellationToken)
    {
        var tiles = await db.CaptureJournal.AsNoTracking()
            .Where(j => j.CaptureId == captureId)
            .OrderBy(j => j.TileX)
            .ThenBy(j => j.TileY)
            .ToListAsync(cancellationToken);
        var pieces = await db.CaptureJournalPieces.AsNoTracking()
            .Where(p => p.CaptureId == captureId)
            .OrderBy(p => p.Id)
            .ToListAsync(cancellationToken);

        List<JournalPiece> PiecesOf(CaptureJournalEntity tile, bool after) =>
            pieces
                .Where(p => p.TileX == tile.TileX && p.TileY == tile.TileY && p.After == after)
                .Select(p => new JournalPiece(Twkb.Read(p.Geometry), ToState(p)))
                .ToList();

        return tiles
            .Select(t => new TileChange(new TileKey(t.TileX, t.TileY), Twkb.Read(t.Footprint), PiecesOf(t, after: false), PiecesOf(t, after: true)))
            .ToList();
    }

    private static CaptureJournalPieceEntity ToEntity(Guid captureId, TileKey tile, bool after, JournalPiece piece) => new()
    {
        CaptureId = captureId,
        TileX = tile.X,
        TileY = tile.Y,
        After = after,
        OwnerId = piece.State.OwnerId,
        Level = (short)piece.State.Level,
        LastVisitAt = piece.State.LastVisitAt,
        LastLevelUpAt = piece.State.LastLevelUpAt,
        ShieldUntil = piece.State.ShieldUntil,
        SiegeUntil = piece.State.SiegeUntil,
        LossWindowSince = piece.State.LossWindowSince,
        LossAttackers = [.. piece.State.LossAttackers.Ids],
        TouchedAt = piece.State.TouchedAt,
        Geometry = Encode(piece.Geometry),
    };

    private static ParcelState ToState(CaptureJournalPieceEntity p) => new()
    {
        OwnerId = p.OwnerId,
        Level = p.Level,
        LastVisitAt = p.LastVisitAt,
        LastLevelUpAt = p.LastLevelUpAt,
        ShieldUntil = p.ShieldUntil,
        SiegeUntil = p.SiegeUntil,
        LossWindowSince = p.LossWindowSince,
        LossAttackers = AttackerSet.Of(p.LossAttackers),
        TouchedAt = p.TouchedAt,
    };

    /// <summary>Геометрия журнала в TWKB. Вершина не на сетке — ошибка движка: захват повторится, потом «не удалось».</summary>
    private static byte[] Encode(Geometry geometry)
    {
        try
        {
            return Twkb.Write(geometry);
        }
        catch (ArgumentException e)
        {
            throw new TerritoryEngineException($"Журнал захвата: {e.Message}");
        }
    }
}
