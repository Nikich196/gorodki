using Gorodki.Api.Features.Segments;
using Gorodki.Domain.Geo;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Segments;

namespace Gorodki.Api.Tests.Segments;

/// <summary>
/// Начальные отрезки «Королей участков» (PLAN.md, §3.13: «6–10 отобранных отрезков по 200–800 м»; предложение на
/// утверждение Никиты, docs/architecture/segments.md). Мемориалы и пересечения с дорогами проверены при отборе по выгрузке
/// OSM — здесь то, что держит сама линия.
/// </summary>
public sealed class SegmentSeedTests
{
    private static readonly SegmentRules Rules = SegmentRules.Default;

    [Fact]
    public void There_are_six_to_ten_segments_with_unique_ids_and_names_all_marked_as_a_proposal()
    {
        var entities = SegmentSeed.Entities().ToList();

        Assert.InRange(entities.Count, 6, 10);
        Assert.Equal(entities.Count, entities.Select(e => e.Id).Distinct().Count());
        Assert.Equal(entities.Count, entities.Select(e => e.Name).Distinct().Count());
        Assert.All(entities, e => Assert.True(e.Proposal, e.Name));
        // В каждой лиге есть свои отрезки: общие и только её.
        Assert.True(entities.Count(e => e.League is null or League.Run) >= 6);
        Assert.True(entities.Count(e => e.League is null or League.Bike) >= 5);
    }

    [Fact]
    public void Every_segment_is_200_to_800_meters_on_the_grid_with_gates_that_do_not_touch()
    {
        foreach (var segment in SegmentSeed.Entities())
        {
            Assert.InRange(segment.LengthMeters, Rules.MinLengthMeters, Rules.MaxLengthMeters);
            Assert.Equal(Utm34.Srid, segment.Line.SRID);
            Assert.True(GeoOps.IsOnGrid(segment.Line), segment.Name);
            Assert.True(segment.Line.IsSimple, $"{segment.Name}: линия сама себя пересекает");
            // Ворота старта и финиша не перекрываются: иначе «по порядку» ничего бы не значило.
            Assert.True(segment.Line.StartPoint.Distance(segment.Line.EndPoint) > 2 * Rules.GateRadiusMeters, segment.Name);
        }
    }

    [Fact]
    public void Every_segment_lies_in_Brest()
    {
        foreach (var entry in SegmentSeed.Entries)
        {
            Assert.True(entry.LatLon.Length >= 4 && entry.LatLon.Length % 2 == 0, entry.Name);
            for (var i = 0; i < entry.LatLon.Length; i += 2)
            {
                Assert.InRange(entry.LatLon[i], 52.03, 52.16);
                Assert.InRange(entry.LatLon[i + 1], 23.60, 23.86);
            }
        }
    }

    [Fact]
    public void Walking_a_seeded_line_end_to_end_counts_as_a_pass()
    {
        // Линия — сама себе путь: точки каждые 3 м вдоль неё, 3 м/с. Так ловится линия, которую не пройти по правилам
        // (например, изгиб, который коридор режет).
        foreach (var segment in SegmentSeed.Entities())
        {
            var course = SegmentSeed.Course(segment.Line);
            var path = new List<PathPoint>();
            var coordinates = segment.Line.Coordinates;
            var t = 0L;
            for (var i = 0; i + 1 < coordinates.Length; i++)
            {
                var length = coordinates[i].Distance(coordinates[i + 1]);
                var steps = Math.Max(1, (int)Math.Ceiling(length / 3));
                for (var s = 0; s < steps; s++, t += (long)(length / steps / 3 * 1000))
                {
                    var f = (double)s / steps;
                    path.Add(new PathPoint(
                        coordinates[i].X + ((coordinates[i + 1].X - coordinates[i].X) * f),
                        coordinates[i].Y + ((coordinates[i + 1].Y - coordinates[i].Y) * f),
                        t));
                }
            }

            path.Add(new PathPoint(coordinates[^1].X, coordinates[^1].Y, t));
            var league = segment.League ?? League.Run;

            var effort = Assert.Single(SegmentEffortDetector.Detect(course, [path], league, Rules));
            Assert.InRange(effort.ElapsedMs, (segment.LengthMeters / 3 * 1000) - 2_000, (segment.LengthMeters / 3 * 1000) + 2_000);
        }
    }
}
