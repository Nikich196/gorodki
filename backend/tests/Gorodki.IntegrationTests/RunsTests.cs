using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gorodki.Api.Features.Runs;
using Gorodki.Api.Infrastructure.Persistence;
using Gorodki.Domain.Leagues;
using Gorodki.Domain.Runs;
using Microsoft.EntityFrameworkCore;
using static Gorodki.IntegrationTests.RunRequests;

namespace Gorodki.IntegrationTests;

/// <summary>Забеги и куски точек: офлайн, повторы, сбитые часы, демо-повтор, чужие забеги и лимиты — на настоящей базе.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class RunsTests(DatabaseFixture database)
{
    private CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Start_can_be_repeated_but_the_same_id_cannot_mean_another_run()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var start = NewStart(api);

        var first = await client.PostAsJsonAsync("/runs", start, Json, Cancel);
        var repeat = await client.PostAsJsonAsync("/runs", start, Json, Cancel);
        var other = await client.PostAsJsonAsync("/runs", start with { StartedAtMs = start.StartedAtMs - 1_000 }, Json, Cancel);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        Assert.Equal("run_conflict", await CodeOf(other));
    }

    [Fact]
    public async Task Chunk_retry_is_harmless_and_overlaps_are_reported()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var start = await StartAsync(api, client);

        var first = await PutChunkAsync(api, client, start, firstSeq: 0, count: 60);
        var repeat = await PutChunkAsync(api, client, start, firstSeq: 0, count: 60);
        var changed = await PutChunkAsync(api, client, start, firstSeq: 0, count: 60, latitude: 52.2);
        var overlapping = await PutChunkAsync(api, client, start, firstSeq: 30, count: 60);
        var next = await PutChunkAsync(api, client, start, firstSeq: 60, count: 60);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(new ChunkReceipt(0, 59, Duplicate: true), await repeat.Content.ReadFromJsonAsync<ChunkReceipt>(Json, Cancel));
        Assert.Equal("chunk_conflict", await CodeOf(changed));
        var problem = await overlapping.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        Assert.Equal("chunk_conflict", problem.GetProperty("code").GetString());
        Assert.Equal(new SeqRange(0, 59), problem.GetProperty("overlaps")[0].Deserialize<SeqRange>(Json));
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);

        var run = await client.GetFromJsonAsync<RunResponse>($"/runs/{start.Id}", Json, Cancel);
        Assert.Equal(new SeqRange[] { new(0, 119) }, run!.Received);
    }

    [Fact]
    public async Task Server_tracks_the_contiguous_start_and_sensor_mark_only_up_to_the_first_hole()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var start = await StartAsync(api, client);

        await PutChunkAsync(api, client, start, firstSeq: 120, count: 60); // за дырой: её отметка датчиков не в счёт
        var afterGap = await ReadinessAsync(start.Id);
        await PutChunkAsync(api, client, start, firstSeq: 0, count: 60);
        var afterFirst = await ReadinessAsync(start.Id);
        await PutChunkAsync(api, client, start, firstSeq: 60, count: 60);
        var afterAll = await ReadinessAsync(start.Id);

        Assert.Equal((-1, 0L), afterGap);
        Assert.Equal((59, start.StartedAtMs + 60_000), afterFirst);
        Assert.Equal((179, start.StartedAtMs + 180_000), afterAll);
    }

    [Fact]
    public async Task First_run_of_a_new_player_is_a_newcomer_run()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var start = await StartAsync(api, client);

        Assert.True((await GetRunAsync(client, start.Id)).Newcomer);
    }

    [Fact]
    public async Task Identical_chunks_sent_at_once_are_stored_once()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var start = await StartAsync(api, client);

        var responses = await Task.WhenAll(
            PutChunkAsync(api, client, start, firstSeq: 0, count: 120),
            PutChunkAsync(api, client, start, firstSeq: 0, count: 120),
            PutChunkAsync(api, client, start, firstSeq: 0, count: 120));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        Assert.All(responses, r => Assert.True(r.IsSuccessStatusCode));
        await using var db = database.CreateContext();
        Assert.Equal(1, await db.RunChunks.CountAsync(c => c.RunId == start.Id, Cancel));
        Assert.Equal(1, await db.Runs.Where(r => r.Id == start.Id).Select(r => r.ChunkCount).SingleAsync(Cancel));
    }

    [Fact]
    public async Task Finish_shows_what_is_missing_and_keeps_the_run_within_4_hours()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var start = await StartAsync(api, client, startedAgo: TimeSpan.FromHours(5));
        await PutChunkAsync(api, client, start, firstSeq: 0, count: 60);
        await PutChunkAsync(api, client, start, firstSeq: 120, count: 60);

        var tooSmall = await FinishAsync(api, client, start, lastSeq: 100, endedAfter: TimeSpan.FromHours(5));
        var finished = await FinishAsync(api, client, start, lastSeq: 199, endedAfter: TimeSpan.FromHours(5));
        var again = await FinishAsync(api, client, start, lastSeq: 250, endedAfter: TimeSpan.FromHours(1));

        Assert.Equal("last_seq_too_small", await CodeOf(tooSmall));
        var run = await finished.Content.ReadFromJsonAsync<RunResponse>(Json, Cancel);
        Assert.Equal(RunStatus.Finished, run!.Status);
        Assert.Equal(start.StartedAtMs + (4 * 3_600_000), run.EndedAtMs);
        Assert.Equal(new SeqRange[] { new(60, 119), new(180, 199) }, run.Missing);

        // Повтор завершения (даже с другими числами) ничего не меняет.
        var repeated = await again.Content.ReadFromJsonAsync<RunResponse>(Json, Cancel);
        Assert.NotNull(repeated);
        Assert.Equal((run.Status, run.EndedAtMs, run.LastSeq), (repeated.Status, repeated.EndedAtMs, repeated.LastSeq));
    }

    [Fact]
    public async Task Only_the_latest_run_stays_active_even_when_an_older_one_arrives_late()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var a = await StartAsync(api, client, startedAgo: TimeSpan.FromMinutes(60));
        var b = await StartAsync(api, client, startedAgo: TimeSpan.FromMinutes(30));

        // Забег C был между A и B, но пришёл последним (телефон был без сети).
        var c = await StartAsync(api, client, startedAgo: TimeSpan.FromMinutes(50));

        var runA = await GetRunAsync(client, a.Id);
        var runB = await GetRunAsync(client, b.Id);
        var runC = await GetRunAsync(client, c.Id);
        Assert.Equal((RunStatus.Abandoned, c.StartedAtMs), (runA.Status, runA.EndedAtMs!.Value));
        Assert.Equal(RunStatus.Active, runB.Status);
        Assert.Equal((RunStatus.Abandoned, b.StartedAtMs), (runC.Status, runC.EndedAtMs!.Value));

        // Телефон всё ещё может честно завершить закрытый сервером забег — но не позже начала следующего.
        var finishedC = await FinishAsync(api, client, c, lastSeq: -1, endedAfter: TimeSpan.FromMinutes(40));
        Assert.Equal(b.StartedAtMs, (await finishedC.Content.ReadFromJsonAsync<RunResponse>(Json, Cancel))!.EndedAtMs);
    }

    [Fact]
    public async Task Phone_clock_a_few_minutes_ahead_is_accepted_and_remembered()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        const long ahead = 3 * 60_000;

        var start = await StartAsync(api, client, clockSkewMs: ahead);
        var chunk = await PutChunkAsync(api, client, start, firstSeq: 0, count: 10, clockSkewMs: ahead);
        var broken = await client.PostAsJsonAsync("/runs", NewStart(api, clockSkewMs: 13 * 3_600_000), Json, Cancel);

        Assert.Equal(HttpStatusCode.Created, chunk.StatusCode);
        Assert.Equal("device_clock_invalid", await CodeOf(broken));
        await using var db = database.CreateContext();
        Assert.Equal(ahead, await db.Runs.Where(r => r.Id == start.Id).Select(r => r.ClockSkewMs).SingleAsync(Cancel));
    }

    [Fact]
    public async Task Demo_replay_on_stage_works_and_players_cannot_replay()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (player, _) = await api.CreatePlayerClientAsync();
        var (demo, _) = await api.CreatePlayerClientAsync(UserRole.Demo);

        var forbidden = await player.PostAsJsonAsync("/runs", NewStart(api) with { Source = RunSource.Replay }, Json, Cancel);
        Assert.Equal("replay_forbidden", await CodeOf(forbidden));

        // Сценарий показа: запись 20 минут проигрывается ×20 за минуту. Время записи сдвинуто в прошлое
        // (начало = сейчас − 20 мин), поэтому точки никогда не оказываются «из будущего».
        var replay = await StartAsync(api, demo, startedAgo: TimeSpan.FromMinutes(20), source: RunSource.Replay);
        for (var minute = 0; minute < 4; minute++)
        {
            api.Time.Advance(TimeSpan.FromSeconds(15));
            var response = await PutChunkAsync(api, demo, replay, firstSeq: minute * 300, count: 300);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
    }

    [Fact]
    public async Task Invalid_chunk_names_the_rule_but_never_echoes_coordinates()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var start = await StartAsync(api, client);
        var request = ChunkRequest(api, start, firstSeq: 0, count: 5);
        var points = request.Points!.ToArray();
        points[3] = points[3] with { Lat = 95.1234567 };

        var response = await client.PutAsJsonAsync($"/runs/{start.Id}/chunks/0", request with { Points = points }, Json, Cancel);
        var body = await response.Content.ReadAsStringAsync(Cancel);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"points[3]\"", body, StringComparison.Ordinal);
        Assert.Contains("\"lat_range\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("95.12", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Someone_elses_run_does_not_exist_for_you()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (owner, _) = await api.CreatePlayerClientAsync();
        var (stranger, _) = await api.CreatePlayerClientAsync();
        var start = await StartAsync(api, owner);

        var put = await PutChunkAsync(api, stranger, start, firstSeq: 0, count: 5);
        var get = await stranger.GetAsync($"/runs/{start.Id}", Cancel);
        var finish = await FinishAsync(api, stranger, start, lastSeq: 4, endedAfter: TimeSpan.FromMinutes(1));

        Assert.Equal("run_not_found", await CodeOf(put));
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal("run_not_found", await CodeOf(finish));
    }

    [Fact]
    public async Task Points_are_accepted_for_a_week_after_the_server_learned_about_the_run()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();
        var start = await StartAsync(api, client);
        var request = ChunkRequest(api, start, firstSeq: 0, count: 5);

        api.Time.Advance(TimeSpan.FromDays(8));
        var late = await client.PutAsJsonAsync(
            $"/runs/{start.Id}/chunks/0", request with { SentAtMs = api.Time.GetUtcNow().ToUnixTimeMilliseconds() }, Json, Cancel);

        Assert.Equal("upload_window_closed", await CodeOf(late));
    }

    [Fact]
    public async Task Deleting_account_stops_new_runs_and_uploads()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, userId) = await api.CreatePlayerClientAsync();
        var start = await StartAsync(api, client);
        await using (var db = database.CreateContext())
        {
            await db.Users.Where(u => u.Id == userId)
                .ExecuteUpdateAsync(set => set.SetProperty(u => u.DeletionRequestedAt, DateTimeOffset.UtcNow), Cancel);
        }

        var put = await PutChunkAsync(api, client, start, firstSeq: 0, count: 5);
        var newRun = await client.PostAsJsonAsync("/runs", NewStart(api), Json, Cancel);

        Assert.Equal("account_deleting", await CodeOf(put));
        Assert.Equal("account_deleting", await CodeOf(newRun));
    }

    [Fact]
    public async Task Too_many_runs_a_day_and_unknown_config_are_refused()
    {
        database.RequireDatabase();
        await using var api = new ApiFactory(database);
        var (client, _) = await api.CreatePlayerClientAsync();

        var unknownConfig = await client.PostAsJsonAsync("/runs", NewStart(api) with { ConfigVersion = 999 }, Json, Cancel);
        for (var i = 0; i < RunLimits.MaxRunsPerDay; i++)
        {
            await StartAsync(api, client, startedAgo: TimeSpan.FromMinutes(100 - i));
        }

        var oneMore = await client.PostAsJsonAsync("/runs", NewStart(api), Json, Cancel);

        Assert.Equal("config_invalid", await CodeOf(unknownConfig));
        Assert.Equal("daily_run_limit", await CodeOf(oneMore));
    }

    // MARK: — вспомогательное

    private async Task<StartRunRequest> StartAsync(
        ApiFactory api, HttpClient client, TimeSpan? startedAgo = null, long clockSkewMs = 0, RunSource source = RunSource.Live)
    {
        var start = NewStart(api, startedAgo, clockSkewMs, source);
        var response = await client.PostAsJsonAsync("/runs", start, Json, Cancel);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return start;
    }

    private Task<HttpResponseMessage> PutChunkAsync(
        ApiFactory api, HttpClient client, StartRunRequest start, int firstSeq, int count, double latitude = 52.0976, long clockSkewMs = 0) =>
        client.PutAsJsonAsync(
            $"/runs/{start.Id}/chunks/{firstSeq}",
            ChunkRequest(api, start, firstSeq, count, latitude, clockSkewMs),
            Json,
            Cancel);

    private Task<HttpResponseMessage> FinishAsync(ApiFactory api, HttpClient client, StartRunRequest start, int lastSeq, TimeSpan endedAfter) =>
        client.PostAsJsonAsync(
            $"/runs/{start.Id}/finish",
            new FinishRunRequest(
                start.StartedAtMs + (long)endedAfter.TotalMilliseconds,
                lastSeq,
                SentAtMs: api.Time.GetUtcNow().ToUnixTimeMilliseconds()),
            Json,
            Cancel);

    private async Task<RunResponse> GetRunAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<RunResponse>($"/runs/{id}", Json, Cancel))!;

    private async Task<(int PrefixEndSeq, long PrefixSensorsMs)> ReadinessAsync(Guid runId)
    {
        await using var db = database.CreateContext();
        var run = await db.Runs.Where(r => r.Id == runId).Select(r => new { r.PrefixEndSeq, r.PrefixSensorsMs }).SingleAsync(Cancel);
        return (run.PrefixEndSeq, run.PrefixSensorsMs);
    }

    private async Task<string?> CodeOf(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Cancel);
        return problem.TryGetProperty("code", out var code) ? code.GetString() : $"HTTP {(int)response.StatusCode}";
    }
}
