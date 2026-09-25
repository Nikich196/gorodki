import Testing

@testable import GameCore

/// Числа для игрока — по-русски при любом языке телефона. На Linux, где идут эти тесты, язык процесса — не русский,
/// поэтому запятая здесь доказывает, что локаль своя, а не телефона.
@Suite("Числа по-русски: запятая и неразрывные пробелы")
struct NumberTextTests {
    private let space = "\u{00A0}"

    @Test("Километры: «1,23 км», а не «1.23 км»")
    func kilometers() {
        #expect(NumberText.kilometers(fromMeters: 1_234, fractionDigits: 2) == "1,23\(space)км")
        #expect(NumberText.kilometers(fromMeters: 0, fractionDigits: 2) == "0,00\(space)км")
        #expect(NumberText.kilometers(fromMeters: 12_345_678, fractionDigits: 1) == "12\(space)345,7\(space)км")
    }

    @Test("Гектары: «0,12 га»")
    func hectares() {
        #expect(NumberText.hectares(fromSquareMeters: 1_234, fractionDigits: 2) == "0,12\(space)га")
        #expect(NumberText.hectares(fromSquareMeters: 12_000, fractionDigits: 1) == "1,2\(space)га")
    }

    @Test("Квадратные метры и целые — разряды через неразрывный пробел: «12 480 м²»")
    func groupsThousands() {
        #expect(NumberText.squareMeters(12_480.4) == "12\(space)480\(space)м²")
        #expect(NumberText.integer(12_480) == "12\(space)480")
        #expect(NumberText.integer(999) == "999")
    }

    @Test("Секунды и дробь: ровно столько знаков, сколько просили")
    func fractionDigits() {
        #expect(NumberText.seconds(3.14, fractionDigits: 1) == "3,1\(space)с")
        #expect(NumberText.decimal(0.6, fractionDigits: 2) == "0,60")
        #expect(NumberText.decimal(2, fractionDigits: 0) == "2")
    }
}
