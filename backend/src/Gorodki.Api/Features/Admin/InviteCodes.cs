namespace Gorodki.Api.Features.Admin;

/// <summary>
/// Инвайт-коды (PLAN.md, D10: регистрация закрытая): 8 символов вида <c>XXXX-XXXX</c> из алфавита Крокфорда. В нём нет
/// букв I, L, O, U — их путают с 1 и 0, когда код диктуют или переписывают с экрана. 32 символа — это 5 бит на символ,
/// 8 символов — 40 бит, около триллиона кодов: живой код не подобрать перебором.
/// </summary>
public static class InviteCodes
{
    /// <summary>Алфавит Крокфорда: цифры и заглавные латинские буквы без I, L, O, U — ровно 32 символа.</summary>
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Новый случайный код вида <c>XXXX-XXXX</c>.</summary>
    public static string New()
    {
        // ЗАДАЧА #114 (Егор): 8 случайных символов из Alphabet и дефис после четвёртого. Случайность — только
        // System.Security.Cryptography.RandomNumberGenerator: у Random следующий код можно предсказать по предыдущим.
        // Тесты — Gorodki.Api.Tests/Admin/InviteCodesTests (без Docker).
        throw new NotImplementedException("ЗАДАЧА #114");
    }
}
