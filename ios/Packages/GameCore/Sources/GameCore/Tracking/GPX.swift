import Foundation

/// След забега в GPX 1.1 (https://www.topografix.com/GPX/1/1/) — для выгрузки в «Файлы» и другие приложения (пункт 6
/// листика, «внешняя FS»). Только точки и их время: высоты и скорости в записи забега нет (у GPX 1.1 скорости нет вовсе).
public enum GPX {
    /// Документ с одним треком из одного сегмента. Точки — по номеру (`seq`), как их записал телефон: порядок, в котором
    /// их отдала очередь, не важен; повтор номера пропускается.
    /// - Parameters:
    ///   - name: название трека (экранируется).
    ///   - points: принятые точки забега.
    public static func document(name: String, points: [TrackPoint]) -> String {
        var ordered: [TrackPoint] = []
        for point in points.sorted(by: { $0.seq < $1.seq }) where point.seq != ordered.last?.seq {
            ordered.append(point)
        }
        var lines = [
            #"<?xml version="1.0" encoding="UTF-8"?>"#,
            #"<gpx version="1.1" creator="Gorodki" xmlns="http://www.topografix.com/GPX/1/1">"#,
        ]
        if let first = ordered.first {
            lines.append("  <metadata><time>\(time(first.timestamp))</time></metadata>")
        }
        lines.append("  <trk>")
        lines.append("    <name>\(escaped(name))</name>")
        lines.append("    <trkseg>")
        for point in ordered {
            lines.append(
                #"      <trkpt lat="\#(degrees(point.coordinate.latitude))" lon="\#(degrees(point.coordinate.longitude))">"#
                    + "<time>\(time(point.timestamp))</time></trkpt>")
        }
        lines.append("    </trkseg>")
        lines.append("  </trk>")
        lines.append("</gpx>")
        return lines.joined(separator: "\n") + "\n"
    }

    /// Имя файла: `gorodki-2026-09-25-0730-run.gpx` — начало забега по часам телефона в поясе `timeZone`.
    public static func fileName(startedAt seconds: Double, league: League, timeZone: TimeZone = .current) -> String {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let parts = calendar.dateComponents(
            [.year, .month, .day, .hour, .minute], from: Date(timeIntervalSince1970: seconds))
        let stamp = String(
            format: "%04d-%02d-%02d-%02d%02d", parts.year ?? 0, parts.month ?? 0, parts.day ?? 0, parts.hour ?? 0,
            parts.minute ?? 0)
        return "gorodki-\(stamp)-\(league.rawValue).gpx"
    }

    /// Текст для XML: пять служебных символов — сущностями.
    static func escaped(_ text: String) -> String {
        var result = ""
        for character in text {
            switch character {
            case "&": result += "&amp;"
            case "<": result += "&lt;"
            case ">": result += "&gt;"
            case "\"": result += "&quot;"
            case "'": result += "&apos;"
            default: result.append(character)
            }
        }
        return result
    }

    /// Градусы десятичной дробью (в GPX — `xsd:decimal`, без экспоненты): 7 знаков — сантиметры.
    static func degrees(_ value: Double) -> String { String(format: "%.7f", value) }

    /// Время UTC с миллисекундами: `2026-09-25T07:30:00.250Z`.
    static func time(_ seconds: Double) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return formatter.string(from: Date(timeIntervalSince1970: seconds))
    }
}
