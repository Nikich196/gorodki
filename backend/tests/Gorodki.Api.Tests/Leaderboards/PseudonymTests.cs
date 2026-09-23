using Gorodki.Api.Features.Leaderboards;

namespace Gorodki.Api.Tests.Leaderboards;

/// <summary>«Игрок #1234» вместо ника без согласия (закон 99-З, PLAN.md §3.16).</summary>
public sealed class PseudonymTests
{
    [Fact]
    public void Pseudonym_is_stable_four_digits_and_differs_between_players()
    {
        var anna = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        var boris = Guid.Parse("00000000-0000-0000-0000-00000000000b");

        Assert.Matches(@"^Игрок #[1-9]\d{3}$", LeaderboardEndpoints.Pseudonym(anna));
        Assert.Equal(LeaderboardEndpoints.Pseudonym(anna), LeaderboardEndpoints.Pseudonym(anna));
        Assert.NotEqual(LeaderboardEndpoints.Pseudonym(anna), LeaderboardEndpoints.Pseudonym(boris));
    }
}
