import ActivityKit
import Platform
import SwiftUI
import WidgetKit

/// Live Activity забега: плашка на экране блокировки и в Dynamic Island. Забег показывает те же числа, что HUD, строками
/// (`RunActivityText`, docs/architecture/run-hud.md): «До замыкания 140 м» без стрелки и «3,21 км · 5:32 /км · +0,80 га
/// тумана»; время идёт само от начала. «Лаборатория» и проверка установки пишут сюда свои строки.
struct RunLiveActivityWidget: Widget {
    var body: some WidgetConfiguration {
        ActivityConfiguration(for: RunActivityAttributes.self) { context in
            RunLockScreenView(context: context)
                .activityBackgroundTint(Color.black.opacity(0.55))
                .activitySystemActionForegroundColor(.white)
        } dynamicIsland: { context in
            DynamicIsland {
                DynamicIslandExpandedRegion(.leading) {
                    Image(systemName: "figure.run")
                        .font(.title2)
                }
                DynamicIslandExpandedRegion(.trailing) {
                    Text(context.attributes.startedAt, style: .timer)
                        .monospacedDigit()
                        .font(.title3.weight(.semibold))
                        .multilineTextAlignment(.trailing)
                }
                DynamicIslandExpandedRegion(.center) {
                    Text(context.state.title)
                        .font(.headline)
                        .lineLimit(1)
                        .minimumScaleFactor(0.7)
                }
                DynamicIslandExpandedRegion(.bottom) {
                    Text(context.state.detail)
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .minimumScaleFactor(0.7)
                }
            } compactLeading: {
                Image(systemName: "figure.run")
            } compactTrailing: {
                Text(context.attributes.startedAt, style: .timer)
                    .monospacedDigit()
                    .frame(maxWidth: 52)
            } minimal: {
                Image(systemName: "figure.run")
            }
        }
    }
}

private struct RunLockScreenView: View {
    let context: ActivityViewContext<RunActivityAttributes>

    var body: some View {
        HStack(spacing: 14) {
            Image(systemName: "figure.run")
                .font(.title)
                .foregroundStyle(.white)
            VStack(alignment: .leading, spacing: 2) {
                Text(context.state.title)
                    .font(.headline)
                    .foregroundStyle(.white)
                Text(context.state.detail)
                    .font(.footnote)
                    .foregroundStyle(.white.opacity(0.8))
                    .lineLimit(2)
            }
            Spacer(minLength: 8)
            Text(context.attributes.startedAt, style: .timer)
                .monospacedDigit()
                .font(.title2.weight(.bold))
                .foregroundStyle(.white)
                .multilineTextAlignment(.trailing)
                .frame(maxWidth: 96, alignment: .trailing)
        }
        .padding(16)
    }
}
