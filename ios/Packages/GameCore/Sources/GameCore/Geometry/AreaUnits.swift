/// Единицы площади, которыми игра говорит с игроком (PLAN.md, §3.5 и §6.10).
public enum AreaUnits {
    /// Квадратных метров в сотке.
    public static let squareMetersPerSotka = 100.0
    /// Квадратных метров в гектаре.
    public static let squareMetersPerHectare = 10_000.0

    public static func sotki(fromSquareMeters squareMeters: Double) -> Double {
        squareMeters / squareMetersPerSotka
    }

    public static func hectares(fromSquareMeters squareMeters: Double) -> Double {
        squareMeters / squareMetersPerHectare
    }
}
