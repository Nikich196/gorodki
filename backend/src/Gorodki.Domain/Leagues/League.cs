namespace Gorodki.Domain.Leagues;

/// <summary>
/// Лига: у бега и велосипеда свои правила античита и своя карта земли (PLAN.md, D16).
/// Числа совпадают с хранимыми в базе (<c>league smallint</c>) и не меняются.
/// </summary>
public enum League : short
{
    /// <summary>Ходьба и бег.</summary>
    Run = 1,

    /// <summary>Велосипед (карта территории — с Сезона 1, по флагу конфига).</summary>
    Bike = 2,
}
