using System.Text.RegularExpressions;
using Gorodki.Api.Features.Admin;

namespace Gorodki.Api.Tests.Admin;

/// <summary>
/// Инвайт-коды — чистая функция <see cref="InviteCodes.New"/>, база не нужна. Задача #114 для Егора: тесты со <c>Skip</c>
/// снимаются вместе с реализацией (CONTRIBUTING.md, «Задачи для друга»).
/// </summary>
public sealed partial class InviteCodesTests
{
    [Fact]
    public void Alphabet_is_32_characters_without_look_alikes()
    {
        // Это проверяет уже договорённость (алфавит Крокфорда), а не реализацию, — поэтому без Skip.
        Assert.Equal(32, InviteCodes.Alphabet.Distinct().Count());
        Assert.DoesNotContain(InviteCodes.Alphabet, c => "ILOU".Contains(c, StringComparison.Ordinal));
    }

    [Fact(Skip = "ЗАДАЧА #114")]
    public void Code_is_two_groups_of_four_from_the_alphabet()
    {
        for (var i = 0; i < 1_000; i++)
        {
            Assert.Matches(Format(), InviteCodes.New());
        }
    }

    [Fact(Skip = "ЗАДАЧА #114")]
    public void Codes_do_not_repeat_and_use_the_whole_alphabet()
    {
        var codes = Enumerable.Range(0, 10_000).Select(_ => InviteCodes.New()).ToList();

        // Кодов около триллиона: 10 000 подряд не совпадают (случайное совпадение — примерно раз на 20 000 запусков теста).
        Assert.Equal(codes.Count, codes.Distinct().Count());
        // Встречаются все 32 символа: генератор не застрял на части алфавита (например, «% 26» вместо длины алфавита).
        Assert.Equal(InviteCodes.Alphabet.Order(), codes.SelectMany(c => c.Replace("-", "", StringComparison.Ordinal)).Distinct().Order());
    }

    /// <summary><c>XXXX-XXXX</c>: цифры и буквы A–Z без I, L, O, U.</summary>
    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$")]
    private static partial Regex Format();
}
