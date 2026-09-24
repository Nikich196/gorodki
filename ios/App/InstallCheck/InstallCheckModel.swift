import Foundation
import Observation
import Platform
import UIKit
import WidgetKit

/// Одна строка проверки: что проверяли, что получилось и насколько это хорошо.
struct CheckItem: Identifiable, Sendable {
    enum Status: Sendable {
        case ok, warning, failed, info
    }

    let id: String
    let title: String
    let value: String
    let status: Status
}

/// Модель экрана «Проверка установки» — спайк S2 (PLAN.md, этап 0).
///
/// После установки через Sideloadly нужно понять, что получилось на самом деле:
/// какой bundle ID достался приложению, до какого числа действует подпись,
/// работает ли App Group и запускается ли расширение с Live Activity.
@MainActor
@Observable
final class InstallCheckModel {
    private(set) var items: [CheckItem] = []
    /// Идентификатор запущенной Live Activity. Сам объект `Activity` храним не здесь — см. `RunActivityController`.
    private(set) var activityID: String?
    private(set) var activityError: String?
    /// Своя проверочная плашка: «Закончить» не должно закрыть плашку идущего забега или прогулки.
    private static let activitySlot = LiveActivitySlot(key: "installCheck.activityID")

    func refresh(now: Date = .now) {
        let info = Bundle.main.infoDictionary ?? [:]
        let profile = ProvisioningProfile.embedded()
        items = [
            systemItem(),
            buildItem(info: info),
            bundleItem(info: info),
            signatureItem(profile: profile, now: now),
            appGroupItem(profile: profile, now: now),
            liveActivitiesItem(),
        ]
        // Если своя Live Activity уже запущена (например, приложение перезапускали), подхватываем её.
        activityID = Self.activitySlot.adopt(running: RunActivityController.runningIDs)
    }

    // MARK: - Live Activity

    func startActivity() {
        activityError = nil
        let state = RunActivityAttributes.ContentState(
            title: "Городки · проверка",
            detail: "Видишь это на экране блокировки — расширение работает."
        )
        do {
            activityID = try RunActivityController.start(startedAt: .now, state: state)
            Self.activitySlot.remember(activityID)
        } catch {
            activityError = error.localizedDescription
        }
    }

    func updateActivity() async {
        guard let activityID else { return }
        let time = Date.now.formatted(date: .omitted, time: .standard)
        let state = RunActivityAttributes.ContentState(
            title: "Городки · проверка",
            detail: "Обновлено из приложения в \(time)."
        )
        await RunActivityController.update(id: activityID, state: state)
    }

    func endActivity() async {
        guard let activityID else { return }
        await RunActivityController.end(id: activityID)
        self.activityID = nil
        Self.activitySlot.forget()
    }

    // MARK: - Строки проверки

    private func systemItem() -> CheckItem {
        let device = UIDevice.current
        return CheckItem(
            id: "system",
            title: "Система",
            value: "\(device.systemName) \(device.systemVersion)",
            status: .info
        )
    }

    private func buildItem(info: [String: Any]) -> CheckItem {
        let flavor = info["GorodkiFlavor"] as? String ?? "—"
        let version = info["CFBundleShortVersionString"] as? String ?? "—"
        let build = info["CFBundleVersion"] as? String ?? "—"
        return CheckItem(id: "build", title: "Сборка", value: "\(flavor) · \(version) (\(build))", status: .info)
    }

    /// Sideloadly может поменять bundle ID. Это важно: вход через Google привязан к bundle ID.
    private func bundleItem(info: [String: Any]) -> CheckItem {
        let expected = info["GorodkiExpectedBundleID"] as? String ?? "—"
        let actual = Bundle.main.bundleIdentifier ?? "—"
        if actual == expected {
            return CheckItem(id: "bundle", title: "Bundle ID", value: actual, status: .ok)
        }
        return CheckItem(
            id: "bundle",
            title: "Bundle ID изменён установщиком",
            value: "\(actual)\nв проекте: \(expected)",
            status: .warning
        )
    }

    private func signatureItem(profile: ProvisioningProfile?, now: Date) -> CheckItem {
        guard let profile else {
            return CheckItem(
                id: "signature",
                title: "Подпись",
                value: "Профиля нет: симулятор или сборка без подписи",
                status: .info
            )
        }
        guard let expires = profile.expirationDate else {
            return CheckItem(id: "signature", title: "Подпись", value: "Срок действия не указан", status: .warning)
        }
        let hoursLeft = expires.timeIntervalSince(now) / 3600
        let status: CheckItem.Status = hoursLeft <= 0 ? .failed : (hoursLeft < 48 ? .warning : .ok)
        let team = profile.teamName.map { " · \($0)" } ?? ""
        return CheckItem(
            id: "signature",
            title: hoursLeft <= 0 ? "Подпись истекла" : "Подпись действует",
            value: "до \(expires.formatted(date: .abbreviated, time: .shortened))\(team)",
            status: status
        )
    }

    /// Пишем отметку в App Group и просим виджет обновиться: виджет должен показать это время.
    private func appGroupItem(profile: ProvisioningProfile?, now: Date) -> CheckItem {
        let groupID = AppGroup.identifier ?? "—"
        guard InstallCheckRecord(lastAppLaunch: now).save() else {
            let granted = profile.map {
                $0.appGroups.isEmpty ? "в профиле групп нет" : $0.appGroups.joined(separator: ", ")
            }
            return CheckItem(
                id: "appGroup",
                title: "App Group недоступна",
                value: [groupID, granted].compactMap { $0 }.joined(separator: "\n"),
                status: .failed
            )
        }
        WidgetCenter.shared.reloadTimelines(ofKind: WidgetKind.installCheck)
        return CheckItem(id: "appGroup", title: "App Group работает", value: groupID, status: .ok)
    }

    private func liveActivitiesItem() -> CheckItem {
        let enabled = RunActivityController.areActivitiesEnabled
        return CheckItem(
            id: "liveActivities",
            title: "Live Activities",
            value: enabled ? "Разрешены" : "Выключены в Настройках → Городки",
            status: enabled ? .ok : .warning
        )
    }
}
