import Foundation
import Testing

@testable import GameCore

/// Логика экранов листика без iOS (пункты 3, 4, 5, 6, 7, 8/9, 13): кадры видео-повтора, QR друга и приглашение,
/// напоминания сезона, размеры файлов, GPX из «Файлов», окно миниатюр галереи.
@Suite("Видео-повтор: след в кадре и голова по пути")
struct TrailReplayTests {
    private let brest = Coordinate(latitude: 52.0976, longitude: 23.7341)

    @Test("След вписан в кадр с полями и не искажён: петля не выходит за поля")
    func fitsInsideFrame() {
        let replay = TrailReplay(
            coordinates: TrailReplay.sampleLoop(around: brest), width: 720, height: 720, padding: 60, frameCount: 90)
        #expect(replay.path.count > 100)
        for point in replay.path {
            #expect((59.99...660.01).contains(point.x), "x = \(point.x)")
            #expect((59.99...660.01).contains(point.y), "y = \(point.y)")
        }
        // Длинная сторона петли — во всю ширину полей.
        let xs = replay.path.map(\.x)
        let ys = replay.path.map(\.y)
        let longest = max((xs.max() ?? 0) - (xs.min() ?? 0), (ys.max() ?? 0) - (ys.min() ?? 0))
        #expect(abs(longest - 600) < 0.5)
        // Петля ~1,5 км (радиус около 220 м).
        #expect((1_000...2_000).contains(replay.totalMeters), "\(replay.totalMeters)")
    }

    @Test("Север — вверх: точка севернее рисуется выше")
    func northIsUp() {
        let north = Coordinate(latitude: brest.latitude + 0.01, longitude: brest.longitude)
        let replay = TrailReplay(coordinates: [brest, north], width: 100, height: 100, padding: 10, frameCount: 2)
        #expect(replay.path[1].y < replay.path[0].y)
        #expect(abs(replay.path[0].x - 50) < 1e-9, "узкий след — по центру")
    }

    @Test("Первый кадр — голова в начале, последний — весь след, путь по кадрам растёт")
    func headMovesAlongPath() throws {
        let replay = TrailReplay(
            coordinates: TrailReplay.sampleLoop(around: brest), width: 400, height: 300, padding: 20, frameCount: 60)
        let first = replay.frame(0)
        #expect(first.head == replay.path.first)
        #expect(first.drawn.count == 1)
        #expect(first.meters == 0)
        let last = replay.frame(59)
        #expect(last.progress == 1)
        let end = try #require(replay.path.last)
        #expect(abs(last.head.x - end.x) < 1e-6 && abs(last.head.y - end.y) < 1e-6)
        #expect(abs(last.meters - replay.totalMeters) < 1e-6)
        var previous = -1.0
        for index in 0..<60 {
            let frame = replay.frame(index)
            #expect(frame.meters > previous)
            #expect(frame.drawn.last == frame.head)
            previous = frame.meters
        }
        #expect(replay.frame(1_000) == last, "номер за пределами — последний кадр")
        #expect(replay.frame(-5) == first)
    }

    @Test("Голова — между точками следа, по доле пути (равномерно, а не по времени)")
    func headInterpolates() {
        let east = Coordinate(latitude: brest.latitude, longitude: brest.longitude + 0.01)
        let replay = TrailReplay(coordinates: [brest, east], width: 200, height: 100, padding: 0, frameCount: 5)
        let middle = replay.frame(2)
        #expect(abs(middle.progress - 0.5) < 1e-9)
        #expect(abs(middle.head.x - 100) < 1e-6)
        #expect(middle.drawn.count == 2)
    }

    @Test("Пустой след, одна точка и стояние на месте — кадры без падения")
    func degenerateTrails() {
        let empty = TrailReplay(coordinates: [], width: 100, height: 50, padding: 5, frameCount: 10)
        #expect(empty.frame(3).drawn.isEmpty)
        #expect(empty.frame(3).head == TrailReplay.Point(x: 50, y: 25))
        let standing = TrailReplay(
            coordinates: [brest, brest, Coordinate(latitude: 99, longitude: 0)], width: 100, height: 100, padding: 5,
            frameCount: 0)
        #expect(standing.path == [TrailReplay.Point(x: 50, y: 50)], "повтор и недопустимая точка отброшены")
        #expect(standing.frameCount == 1)
        #expect(standing.frame(0).meters == 0)
    }
}

@Suite("QR друга и приглашение")
struct FriendLinkTests {
    @Test("Игрок: ссылка с ником по-русски читается обратно")
    func playerRoundTrip() {
        let link = FriendLink.player(id: "0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b", name: "Бегун-1234")
        #expect(link.url.hasPrefix("gorodki://friend?id=0199a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b&name="))
        #expect(FriendLink.parse(link.url) == link)
        #expect(FriendLink.parse(FriendLink.player(id: "abc", name: nil).url) == .player(id: "abc", name: nil))
    }

    @Test("Приглашение: код приводится к заглавным, неверный код — не наш QR")
    func inviteCodes() {
        #expect(FriendLink.invite(code: "ABCD-2345").url == "gorodki://invite?code=ABCD-2345")
        #expect(FriendLink.parse(" gorodki://invite?code=abcd-2345 ") == .invite(code: "ABCD-2345"))
        #expect(FriendLink.parse("gorodki://invite?code=ABC-2345") == nil)
        #expect(FriendLink.parse("gorodki://invite") == nil)
    }

    @Test("Чужие коды — не наши: сайт, текст, другая схема, пустой номер")
    func foreignCodes() {
        #expect(FriendLink.parse("https://example.com/friend?id=1") == nil)
        #expect(FriendLink.parse("просто текст") == nil)
        #expect(FriendLink.parse("gorodki://friend?id=") == nil)
        #expect(FriendLink.parse("gorodki://clan?id=1") == nil)
    }

    @Test("Код приглашения: две группы по четыре латинские буквы или цифры")
    func wellFormedCodes() {
        #expect(InviteText.isWellFormed("ABCD-2345"))
        #expect(!InviteText.isWellFormed("ABCD2345"))
        #expect(!InviteText.isWellFormed("АБВГ-2345"), "кириллица — не код")
        #expect(!InviteText.isWellFormed("ABCD-23456"))
        #expect(InviteText.normalizedCode("  abcd-2345\n") == "ABCD-2345")
    }

    @Test("Сообщение другу: имя, код и ник; без имени и с неверным кодом — честно")
    func message() {
        let text = InviteText.message(friendName: "Аня", inviteCode: "abcd-2345", senderName: "Бегун-1234")
        #expect(text.hasPrefix("Привет, Аня!\n"))
        #expect(text.contains("Код приглашения: ABCD-2345"))
        #expect(text.hasSuffix("Меня в игре зовут Бегун-1234."))
        let bare = InviteText.message(friendName: " ", inviteCode: "нет", senderName: nil)
        #expect(bare.hasPrefix("Привет!\n"))
        #expect(!bare.contains("Код приглашения:"))
        #expect(bare.contains("код пришлю"))
    }
}

@Suite("Напоминания: конец сезона в Календаре и уведомлением")
struct RemindersTests {
    /// Сезон 0 из contracts/samples/seasons.json: 16.11 00:00 — 30.11 00:00 по Минску.
    private let season0 = SeasonInfo(
        number: 0, name: "Сезон 0 (бета)", startsAtMs: 1_794_776_400_000, endsAtMs: 1_795_986_000_000)
    private let season1 = SeasonInfo(number: 1, name: "Сезон 1", startsAtMs: 1_795_986_000_000, endsAtMs: nil)
    private let minsk = TimeZone(identifier: "Europe/Minsk") ?? TimeZone(secondsFromGMT: 3 * 3_600) ?? .current

    @Test("Событие — последний час сезона, напоминание за сутки; у бессрочного сезона события нет")
    func calendarEvent() throws {
        let event = try #require(Reminders.calendarEvent(for: season0))
        #expect(event.title == "Городки: конец сезона «Сезон 0 (бета)»")
        #expect(event.end == 1_795_986_000)
        #expect(event.start == 1_795_986_000 - 3_600)
        #expect(event.alarmBefore == 86_400)
        #expect(event.notes.contains("Зал славы"))
        #expect(Reminders.calendarEvent(for: season1) == nil)
    }

    @Test("Ближайший конец — текущий сезон; прошедшие и бессрочные не в счёт")
    func upcomingEnd() {
        #expect(Reminders.upcomingEnd(in: [season1, season0], now: 1_795_000_000) == season0)
        #expect(Reminders.upcomingEnd(in: [season1, season0], now: 1_796_000_000) == nil)
    }

    @Test("«Сезон заканчивается завтра» — 28.11 в 19:00 по Минску: последний день сезона — 29.11")
    func endingTomorrow() throws {
        let reminder = try #require(Reminders.seasonEndingTomorrow(season0, now: 1_795_000_000, timeZone: minsk))
        // 28.11.2026 19:00 по Минску (UTC+3) = 16:00 UTC.
        #expect(reminder.fireAt == 1_795_881_600)
        #expect(reminder.id == "season.0.ending")
        #expect(reminder.body.contains("«Сезон 0 (бета)»"))
        #expect(Reminders.seasonEndingTomorrow(season0, now: 1_795_881_600, timeZone: minsk) == nil, "время прошло")
        #expect(Reminders.seasonEndingTomorrow(season1, now: 0, timeZone: minsk) == nil)
    }

    @Test("«Забег всё ещё идёт» — через два часа, без рода в тексте")
    func runStillGoing() {
        let reminder = Reminders.runStillGoing(now: 1_000)
        #expect(reminder.fireAt == 1_000 + 7_200)
        #expect(reminder.id == Reminders.runStillGoingID)
        #expect(!reminder.body.contains("закончил"))
    }
}

@Suite("Размеры файлов, GPX из «Файлов», окно миниатюр")
struct StorageAndMediaTests {
    private let space = "\u{00A0}"

    @Test("Байты — десятичными единицами, как «Хранилище iPhone»")
    func bytes() {
        #expect(NumberText.bytes(0) == "0\(space)Б")
        #expect(NumberText.bytes(812) == "812\(space)Б")
        #expect(NumberText.bytes(4_200_000) == "4,2\(space)МБ")
        #expect(NumberText.bytes(37_400_000) == "37\(space)МБ")
        #expect(NumberText.bytes(999_600) == "1,0\(space)МБ", "не «1 000 КБ»")
        #expect(NumberText.bytes(1_300_000_000) == "1,3\(space)ГБ")
        #expect(NumberText.bytes(-5) == "0\(space)Б")
    }

    @Test("GPX: точки трека по порядку, кавычки любые, неразборчивые пропущены")
    func gpxCoordinates() {
        let document = """
            <gpx><trk><trkseg>
            <trkpt lat="52.1" lon="23.7"><time>2026-09-25T07:30:00Z</time></trkpt>
            <trkpt lon='23.71'  lat = '52.11' />
            <trkpt lat="сломано" lon="23.72"></trkpt>
            <trkpt lat="95" lon="23.72"></trkpt>
            <trkpts lat="1" lon="1"/>
            <rtept lat="1" lon="1"/>
            </trkseg></trk></gpx>
            """
        #expect(
            GPX.trackCoordinates(in: document) == [
                Coordinate(latitude: 52.1, longitude: 23.7), Coordinate(latitude: 52.11, longitude: 23.71),
            ])
        #expect(GPX.trackCoordinates(in: "не GPX").isEmpty)
    }

    @Test("Свой GPX читается обратно")
    func gpxRoundTrip() {
        let points = (0..<5).map {
            TrackPoint(
                seq: $0, coordinate: Coordinate(latitude: 52.1 + Double($0) * 0.001, longitude: 23.7),
                timestamp: 1_790_000_000 + Double($0), horizontalAccuracy: 5)
        }
        let parsed = GPX.trackCoordinates(in: GPX.document(name: "lat=\"1\"", points: points))
        #expect(parsed.count == 5)
        #expect(abs((parsed.last?.latitude ?? 0) - 52.104) < 1e-9)
    }

    @Test("Окно миниатюр: запас по краям, при прокрутке — новые готовятся, ушедшие отпускаются")
    func preheatWindow() {
        var window = PreheatWindow()
        let first = window.update(visible: 0...8, total: 100, margin: 6)
        #expect(first.start == Array(0..<15))
        #expect(first.stop.isEmpty)
        let scrolled = window.update(visible: 12...20, total: 100, margin: 6)
        #expect(scrolled.start == Array(15..<27))
        #expect(scrolled.stop == Array(0..<6))
        #expect(window.range == 6..<27)
        let same = window.update(visible: nil, total: 100, margin: 6)
        #expect(same.start.isEmpty && same.stop.isEmpty, "ничего не видно — окно прежнее")
        let shrunk = window.update(visible: 0...3, total: 2, margin: 6)
        #expect(shrunk.start == [0, 1])
        #expect(shrunk.stop == Array(6..<27))
        #expect(window.range == 0..<2, "медиатека уменьшилась — окно не выходит за неё")
    }
}
