using Gorodki.Api.Features.Config;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Config;
using Gorodki.Domain.Runs;
using Microsoft.EntityFrameworkCore;

namespace Gorodki.Api.Features.Runs;

/// <summary>
/// Судья отрезков по всему непрерывному началу следа забега — общий для захватов и тумана: одни и те же точки, та же версия
/// правил, с которой забег начат, и правила новичка (PLAN.md, §3.9, слой 3).
/// </summary>
public sealed class RunJudgements(AppDbContext db, GameConfigStore configs)
{
    public async Task<(GameConfig Rules, TrackJudging.RunJudgement Judgement)> JudgeAsync(RunEntity run, CancellationToken cancellationToken)
    {
        var config = await configs.GetAsync(run.ConfigVersion, cancellationToken)
            ?? throw new InvalidOperationException($"Нет версии конфига {run.ConfigVersion}, с которой начат забег.");
        var encoded = await db.RunChunks.AsNoTracking()
            .Where(c => c.RunId == run.Id && c.LastSeq <= run.PrefixEndSeq)
            .OrderBy(c => c.FirstSeq)
            .Select(c => c.Points)
            .ToListAsync(cancellationToken);
        var judgement = TrackJudging.JudgeRun(
            config.Rules.JudgeRulesFor(run.League, run.Newcomer),
            encoded.Select(bytes => TrackChunkCodec.Decode(bytes)).ToList());
        return (config.Rules, judgement);
    }
}
