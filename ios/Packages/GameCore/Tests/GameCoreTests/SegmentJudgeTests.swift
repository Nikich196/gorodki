import Testing

@testable import GameCore

@Suite("Античит на телефоне: слои 1 и 2 (PLAN.md §3.9)")
struct SegmentJudgeTests {
    /// Прогоняет точки через судью и возвращает первое решение, отличное от «принято», с номером точки.
    private func firstIssue(
        _ points: [TrackPoint],
        judge: inout SegmentJudge,
        motion: [MotionSample] = [],
        steps: [PedometerSample] = []
    ) -> (index: Int, verdict: JudgeVerdict)? {
        var motionQueue = motion
        var stepQueue = steps
        for (index, point) in points.enumerated() {
            while let sample = motionQueue.first, sample.timestamp <= point.timestamp {
                judge.record(sample)
                motionQueue.removeFirst()
            }
            while let sample = stepQueue.first, sample.end <= point.timestamp {
                judge.record(sample)
                stepQueue.removeFirst()
            }
            let verdict = judge.judge(point, now: point.timestamp)
            if verdict != .accepted {
                return (index, verdict)
            }
        }
        return nil
    }

    // MARK: Лига «Бег»

    @Test("Ходьба и бег в лиге «Бег» принимаются")
    func walkingAndRunningAreAccepted() {
        for speed in [1.4, 3.0, 4.5] {
            var sim = TrackSimulator()
            var judge = SegmentJudge(league: .run)
            #expect(firstIssue(sim.straight(from: (0, 0), speed: speed, seconds: 400), judge: &judge) == nil)
        }
    }

    @Test("Машина в лиге «Бег» рвёт след после 30 с быстрее 25 км/ч")
    func carInRunLeague() throws {
        var sim = TrackSimulator()
        var judge = SegmentJudge(league: .run)
        let issue = try #require(firstIssue(sim.straight(from: (0, 0), speed: 15, seconds: 90), judge: &judge))
        #expect(issue.verdict == .segmentBroken(.tooFast))
        #expect(issue.index >= 30)
    }

    @Test("Долгий быстрый бег: предел 19 км/ч за 5 минут")
    func sustainedFastRun() throws {
        var sim = TrackSimulator()
        var judge = SegmentJudge(league: .run)
        // 6 м/с = 21,6 км/ч: меньше 25 за 30 с, но больше 19 за 5 минут.
        let issue = try #require(firstIssue(sim.straight(from: (0, 0), speed: 6, seconds: 400), judge: &judge))
        #expect(issue.verdict == .segmentBroken(.tooFast))
        #expect(issue.index >= 300)
    }

    @Test("Датчики «транспорт» 20 с подряд рвут след даже на малой скорости")
    func vehicleByMotion() throws {
        var sim = TrackSimulator()
        var judge = SegmentJudge(league: .run)
        let points = sim.straight(from: (0, 0), speed: 2, seconds: 60)
        let motion = [MotionSample(timestamp: points[10].timestamp, activity: .automotive)]
        let issue = try #require(firstIssue(points, judge: &judge, motion: motion))
        #expect(issue.verdict == .segmentBroken(.vehicle))
        #expect(issue.index >= 30)
    }

    @Test("Записи датчиков не по порядку: судья читает их по времени — как сервер (он их сортирует)")
    func motionOutOfOrder() throws {
        var sim = TrackSimulator()
        var judge = SegmentJudge(league: .run)
        let points = sim.straight(from: (0, 0), speed: 2, seconds: 60)
        // «Транспорт» с 10-й секунды пришёл раньше, чем более ранняя «ходьба» с 5-й.
        judge.record(MotionSample(timestamp: points[10].timestamp, activity: .automotive))
        judge.record(MotionSample(timestamp: points[5].timestamp, activity: .walking))
        let issue = try #require(firstIssue(points, judge: &judge))
        #expect(issue.verdict == .segmentBroken(.vehicle))
    }

    @Test("Велосипед в лиге «Бег»: разрыв с причиной «велосипед» (предложить лигу «Вело»)")
    func cyclingInRunLeague() throws {
        var sim = TrackSimulator()
        var judge = SegmentJudge(league: .run)
        let points = sim.straight(from: (0, 0), speed: 4, seconds: 60)
        let motion = [MotionSample(timestamp: points[5].timestamp, activity: .cycling)]
        let issue = try #require(firstIssue(points, judge: &judge, motion: motion))
        #expect(issue.verdict == .segmentBroken(.cycling))
        #expect(issue.index >= 35)
    }

    @Test("Движение без шагов 20 с — разрыв")
    func movingWithoutSteps() throws {
        var sim = TrackSimulator()
        var judge = SegmentJudge(league: .run)
        let points = sim.straight(from: (0, 0), speed: 2, seconds: 60)
        let steps = stride(from: 0, to: 60, by: 5).map {
            PedometerSample(start: points[$0].timestamp, end: points[$0].timestamp + 5, steps: 0)
        }
        let issue = try #require(firstIssue(points, judge: &judge, steps: steps))
        #expect(issue.verdict == .segmentBroken(.noSteps))
    }

    @Test("Длина шага: 6 м — не человек, 0,75 м — нормально, шагомер «не знает» — не мешает")
    func strideLength() {
        func run(stepsPerFiveSeconds: Int?) -> (index: Int, verdict: JudgeVerdict)? {
            var sim = TrackSimulator()
            var judge = SegmentJudge(league: .run)
            let points = sim.straight(from: (0, 0), speed: 2, seconds: 60)
            let steps = stride(from: 0, to: 60, by: 5).map {
                PedometerSample(start: points[$0].timestamp, end: points[$0].timestamp + 5, steps: stepsPerFiveSeconds)
            }
            return firstIssue(points, judge: &judge, steps: steps)
        }

        // 2 м/с × 5 с = 10 м: 2 шага → 5 м на шаг; 13 шагов → 0,77 м.
        #expect(run(stepsPerFiveSeconds: 2)?.verdict == .segmentBroken(.strideOutOfRange))
        #expect(run(stepsPerFiveSeconds: 13) == nil)
        #expect(run(stepsPerFiveSeconds: nil) == nil)
    }

    // MARK: Слой 1

    @Test("Точка с плохой точностью, устаревшая или из прошлого — отбрасывается, след не рвётся")
    func badPointsAreIgnored() {
        var sim = TrackSimulator()
        var judge = SegmentJudge(league: .run)
        let first = sim.next(east: 0, north: 0)
        #expect(judge.judge(first, now: first.timestamp) == .accepted)

        let blurry = sim.next(east: 2, north: 0, accuracy: 40)
        #expect(judge.judge(blurry, now: blurry.timestamp) == .ignored(.poorAccuracy))

        let stale = sim.next(east: 4, north: 0)
        #expect(judge.judge(stale, now: stale.timestamp + 15) == .ignored(.staleFix))

        var backwards = sim.next(east: 6, north: 0)
        backwards.timestamp = first.timestamp - 1
        #expect(judge.judge(backwards, now: first.timestamp) == .ignored(.timeWentBackwards))
    }

    @Test("Скачок на 500 м за 2 с — «телепорт», след рвётся")
    func teleport() {
        var sim = TrackSimulator()
        var judge = SegmentJudge(league: .run)
        let a = sim.next(east: 0, north: 0)
        _ = judge.judge(a, now: a.timestamp)
        sim.time += 1
        let b = sim.next(east: 500, north: 0)
        #expect(judge.judge(b, now: b.timestamp) == .segmentBroken(.teleport))
    }

    // MARK: Лига «Вело»

    @Test("Велосипед 20 км/ч в лиге «Вело» принимается")
    func cyclingIsAccepted() {
        var sim = TrackSimulator()
        var judge = SegmentJudge(league: .bike)
        #expect(firstIssue(sim.straight(from: (0, 0), speed: 5.6, seconds: 400), judge: &judge) == nil)
    }

    @Test("50 км/ч в лиге «Вело» — разрыв (предел 48 км/ч за 30 с)")
    func tooFastForBike() throws {
        var sim = TrackSimulator()
        var judge = SegmentJudge(league: .bike)
        let issue = try #require(firstIssue(sim.straight(from: (0, 0), speed: 13.9, seconds: 90), judge: &judge))
        #expect(issue.verdict == .segmentBroken(.tooFast))
    }

    @Test("Разгон как у машины: с 10 до 40 км/ч за 4 с")
    func carLaunch() throws {
        var sim = TrackSimulator()
        var judge = SegmentJudge(league: .bike)
        var points = sim.straight(from: (0, 0), speed: 2.8, seconds: 20, reportSpeed: true)
        var east = 2.8 * 20
        for speed in [5.0, 7.5, 9.5, 11.1, 11.1] {
            east += speed
            points.append(sim.next(east: east, north: 0, speed: speed))
        }
        let issue = try #require(firstIssue(points, judge: &judge))
        #expect(issue.verdict == .segmentBroken(.carLaunch))
    }

    @Test("Датчики «транспорт» большую часть 2 минут при 30 км/ч — разрыв в лиге «Вело»")
    func vehicleShareInBikeLeague() throws {
        var sim = TrackSimulator()
        var judge = SegmentJudge(league: .bike)
        let points = sim.straight(from: (0, 0), speed: 8.3, seconds: 150)
        let motion = [MotionSample(timestamp: points[0].timestamp, activity: .automotive)]
        let issue = try #require(firstIssue(points, judge: &judge, motion: motion))
        #expect(issue.verdict == .segmentBroken(.vehicle))
        #expect(issue.index >= 60)
    }
}
