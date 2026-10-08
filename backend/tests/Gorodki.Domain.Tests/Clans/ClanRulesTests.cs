using Gorodki.Domain.Clans;

namespace Gorodki.Domain.Tests.Clans;

/// <summary>Правила кланов (PLAN.md, §3.3, §3.6): название, кто кого исключает, кто становится лидером, оттенок.</summary>
public sealed class ClanRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Бегуны БрГТУ")]
    [InlineData("abc")]
    [InlineData("МС-10")]
    [InlineData("Клан 12345678")]
    [InlineData("Рубля и барсука")] // «бля» и «сука» — внутри обычных слов, не в начале
    [InlineData("Застрахуем")]
    [InlineData("Ўсходні вецер")] // белорусская «ў» — буква
    public void Good_names_pass(string name)
    {
        Assert.Equal(ClanNameProblem.None, ClanRules.Check(name));
    }

    [Theory]
    [InlineData("ab", ClanNameProblem.Length)]
    [InlineData("   ", ClanNameProblem.Length)]
    [InlineData("  ab  ", ClanNameProblem.Length)] // пробелы по краям не в счёт
    [InlineData("ааааааааааааааааааааааааа", ClanNameProblem.Length)] // 25
    [InlineData("Клан!", ClanNameProblem.Characters)]
    [InlineData("Клан_1", ClanNameProblem.Characters)]
    [InlineData("Клан\t1", ClanNameProblem.Characters)]
    [InlineData("Сука клан", ClanNameProblem.Profanity)]
    [InlineData("Нахуй всех", ClanNameProblem.Profanity)]
    [InlineData("xyй", ClanNameProblem.Profanity)] // латиница вместо кириллицы
    [InlineData("пи зда", ClanNameProblem.Profanity)]
    [InlineData("Ёбаный клан", ClanNameProblem.Profanity)]
    [InlineData("FUCK team", ClanNameProblem.Profanity)]
    public void Bad_names_are_rejected_with_a_reason(string name, ClanNameProblem expected)
    {
        Assert.Equal(expected, ClanRules.Check(name));
    }

    [Fact]
    public void Exactly_three_and_twenty_four_letters_are_allowed()
    {
        Assert.Equal(ClanNameProblem.None, ClanRules.Check("абв"));
        Assert.Equal(ClanNameProblem.None, ClanRules.Check(new string('а', 24)));
    }

    [Fact]
    public void Names_are_unique_ignoring_case_and_extra_spaces()
    {
        Assert.Equal(ClanRules.Normalize("Бегуны  БрГТУ "), ClanRules.Normalize("бегуны брГТУ"));
        Assert.Equal("Бегуны БрГТУ", ClanRules.Clean("  Бегуны   БрГТУ "));
        Assert.NotEqual(ClanRules.Normalize("Бегуны-1"), ClanRules.Normalize("Бегуны 1"));
    }

    [Theory]
    [InlineData(ClanRole.Leader, ClanRole.Officer, true)]
    [InlineData(ClanRole.Leader, ClanRole.Member, true)]
    [InlineData(ClanRole.Leader, ClanRole.Leader, false)] // себя — только выходом
    [InlineData(ClanRole.Officer, ClanRole.Member, true)]
    [InlineData(ClanRole.Officer, ClanRole.Officer, false)]
    [InlineData(ClanRole.Officer, ClanRole.Leader, false)]
    [InlineData(ClanRole.Member, ClanRole.Member, false)]
    public void Leader_removes_anyone_officer_only_members(ClanRole actor, ClanRole target, bool allowed)
    {
        Assert.Equal(allowed, ClanRules.CanRemove(actor, target));
    }

    [Fact]
    public void Leadership_passes_to_the_oldest_officer_otherwise_to_the_oldest_member()
    {
        var oldMember = new ClanMemberInfo(Guid.NewGuid(), ClanRole.Member, T0);
        var youngOfficer = new ClanMemberInfo(Guid.NewGuid(), ClanRole.Officer, T0.AddDays(3));
        var oldOfficer = new ClanMemberInfo(Guid.NewGuid(), ClanRole.Officer, T0.AddDays(1));
        var youngMember = new ClanMemberInfo(Guid.NewGuid(), ClanRole.Member, T0.AddDays(5));

        Assert.Equal(oldOfficer.UserId, ClanRules.Successor([youngMember, youngOfficer, oldMember, oldOfficer]));
        Assert.Equal(oldMember.UserId, ClanRules.Successor([youngMember, oldMember]));
        Assert.Null(ClanRules.Successor([]));
    }

    [Fact]
    public void A_new_clan_gets_a_free_hue_and_with_more_than_twelve_the_rarest_one()
    {
        Assert.Equal(0, ClanRules.PickHue([]));
        Assert.Equal(2, ClanRules.PickHue([0, 1, 3]));
        Assert.Equal(5, ClanRules.PickHue([.. Enumerable.Range(0, 12), .. Enumerable.Range(0, 12).Where(h => h != 5)]));
        Assert.Equal([2, 4, 5, 6, 7, 8, 9, 10, 11], ClanRules.FreeHues([0, 1, 3, 3]));
        Assert.Empty(ClanRules.FreeHues(Enumerable.Range(0, 12)));
    }
}
