import Foundation
import Testing

@testable import GameCore

@Suite("GPX 1.1: след забега для «Файлов»")
struct GPXTests {
    private static func point(_ seq: Int, lat: Double = 52.0976, lon: Double = 23.7341, at time: Double? = nil)
        -> TrackPoint
    {
        TrackPoint(
            seq: seq, coordinate: Coordinate(latitude: lat, longitude: lon),
            timestamp: time ?? 1_790_000_000 + Double(seq), horizontalAccuracy: 5)
    }

    @Test("Документ целиком: заголовок, трек, точки с временем UTC")
    func wholeDocument() {
        let gpx = GPX.document(name: "Забег", points: [Self.point(0, at: 1_790_000_000.25), Self.point(1, lon: -0.5)])
        #expect(
            gpx == """
                <?xml version="1.0" encoding="UTF-8"?>
                <gpx version="1.1" creator="Gorodki" xmlns="http://www.topografix.com/GPX/1/1">
                  <metadata><time>2026-09-21T14:13:20.250Z</time></metadata>
                  <trk>
                    <name>Забег</name>
                    <trkseg>
                      <trkpt lat="52.0976000" lon="23.7341000"><time>2026-09-21T14:13:20.250Z</time></trkpt>
                      <trkpt lat="52.0976000" lon="-0.5000000"><time>2026-09-21T14:13:21.000Z</time></trkpt>
                    </trkseg>
                  </trk>
                </gpx>

                """)
    }

    @Test("Служебные символы XML в названии — сущностями")
    func escaping() {
        let gpx = GPX.document(name: #"Tom & "Jerry" <'бег'>"#, points: [])
        #expect(gpx.contains("<name>Tom &amp; &quot;Jerry&quot; &lt;&apos;бег&apos;&gt;</name>"))
        #expect(!gpx.contains("<metadata>"))  // без точек времени нет
    }

    @Test("Точки — по номеру, хоть очередь отдала их вперемешку; повтор номера пропускается")
    func orderBySeq() {
        let gpx = GPX.document(
            name: "x",
            points: [Self.point(2, lat: 2), Self.point(0, lat: 0), Self.point(1, lat: 1), Self.point(1, lat: 9)])
        let lats = gpx.split(separator: "\n").filter { $0.contains("<trkpt") }.map { line in
            line.split(separator: "\"")[1]
        }
        #expect(lats == ["0.0000000", "1.0000000", "2.0000000"])
    }

    @Test("Малые градусы — без экспоненты (xsd:decimal)")
    func noExponent() {
        #expect(GPX.degrees(0.00001) == "0.0000100")
        #expect(GPX.degrees(-179.9999999) == "-179.9999999")
    }

    @Test("Имя файла — по началу забега в поясе телефона")
    func fileName() throws {
        let minsk = try #require(TimeZone(identifier: "Europe/Minsk"))
        #expect(
            GPX.fileName(startedAt: 1_790_000_000, league: .run, timeZone: minsk) == "gorodki-2026-09-21-1713-run.gpx")
    }
}
