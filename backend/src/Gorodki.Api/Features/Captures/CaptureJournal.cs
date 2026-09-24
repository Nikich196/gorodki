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

    /// <summary>
    /// Добавляет в контекст строки точного отката (<see cref="ParcelSwap"/>) — только для тайлов, у которых уже есть запись
    /// журнала (внешний ключ). Номера вставленных строк известны только после сохранения земли, поэтому это второе
    /// сохранение в той же транзакции (<see cref="CaptureProcessor"/>).
    /// </summary>
    public static void AddParcels(AppDbContext db, Guid captureId, DateTimeOffset appliedAt, IEnumerable<ParcelSwap> swaps)
    {
        foreach (var swap in swaps)
        {
            db.CaptureJournalParcels.AddRange(swap.Replaced.Select(row => ToEntity(captureId, swap.Tile, appliedAt, replaced: true, row)));
            db.CaptureJournalParcels.AddRange(swap.Written.Select(row => ToEntity(captureId, swap.Tile, appliedAt, replaced: false, row)));
        }
    }

    /// <summary>
    /// Запись журнала захвата в одном тайле для публичной проекции: строки точного отката (<c>null</c> — их нет: захват
    /// записан до них, контур не сохранился без потерь, строки стёрты) и запись «до/после» для запасного пути. Геометрия
    /// здесь не разбирается: испорченная запись «до/после» не мешает точному откату, а испорченная строка точного отката —
    /// запасному пути (<see cref="ExactUndo.Project"/>).
    /// </summary>
    public static async Task<HiddenTileChange> LoadTileAsync(AppDbContext db, Guid captureId, TileKey tile, CancellationToken cancellationToken)
    {
        var journal = await db.CaptureJournal.AsNoTracking()
            .SingleAsync(j => j.CaptureId == captureId && j.TileX == tile.X && j.TileY == tile.Y, cancellationToken);
        var pieces = await db.CaptureJournalPieces.AsNoTracking()
            .Where(p => p.CaptureId == captureId && p.TileX == tile.X && p.TileY == tile.Y)
            .OrderBy(p => p.Id)
            .ToListAsync(cancellationToken);
        var rows = await db.CaptureJournalParcels.AsNoTracking()
            .Where(r => r.CaptureId == captureId && r.TileX == tile.X && r.TileY == tile.Y)
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken);

        var swap = rows.Count == 0
            ? null
            : new ParcelSwap(
                tile,
                [.. rows.Where(r => r.Replaced).Select(ToJournalParcel)],
                [.. rows.Where(r => !r.Replaced).Select(ToJournalParcel)]);
        return new HiddenTileChange(swap, () => ToChange(journal, pieces));
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

        return tiles
            .Select(t => ToChange(t, [.. pieces.Where(p => p.TileX == t.TileX && p.TileY == t.TileY)]))
            .ToList();
    }

    /// <exception cref="FormatException">Геометрия записи испорчена.</exception>
    private static TileChange ToChange(CaptureJournalEntity tile, IReadOnlyList<CaptureJournalPieceEntity> pieces)
    {
        List<JournalPiece> PiecesOf(bool after) =>
            [.. pieces.Where(p => p.After == after).Select(p => new JournalPiece(Twkb.Read(p.Geometry), ToState(p)))];

        return new TileChange(new TileKey(tile.TileX, tile.TileY), Twkb.Read(tile.Footprint), PiecesOf(after: false), PiecesOf(after: true));
    }

    private static CaptureJournalParcelEntity ToEntity(Guid captureId, TileKey tile, DateTimeOffset appliedAt, bool replaced, JournalParcel row) => new()
    {
        CaptureId = captureId,
        TileX = tile.X,
        TileY = tile.Y,
        AppliedAt = appliedAt,
        Replaced = replaced,
        ParcelId = row.ParcelId,
        OwnerId = row.State.OwnerId,
        Level = (short)row.State.Level,
        LastVisitAt = row.State.LastVisitAt,
        LastLevelUpAt = row.State.LastLevelUpAt,
        ShieldUntil = row.State.ShieldUntil,
        SiegeUntil = row.State.SiegeUntil,
        LossWindowSince = row.State.LossWindowSince,
        LossAttackers = [.. row.State.LossAttackers.Ids],
        Geometry = row.Geometry,
    };

    private static JournalParcel ToJournalParcel(CaptureJournalParcelEntity row) => new(
        row.ParcelId,
        new ParcelState
        {
            OwnerId = row.OwnerId,
            Level = row.Level,
            LastVisitAt = row.LastVisitAt,
            LastLevelUpAt = row.LastLevelUpAt,
            ShieldUntil = row.ShieldUntil,
            SiegeUntil = row.SiegeUntil,
            LossWindowSince = row.LossWindowSince,
            LossAttackers = AttackerSet.Of(row.LossAttackers),
        },
        row.Geometry);

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
