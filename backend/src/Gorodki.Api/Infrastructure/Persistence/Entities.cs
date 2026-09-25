using Gorodki.Domain.Leagues;
using NetTopologySuite.Geometries;

namespace Gorodki.Api.Infrastructure.Persistence;

// Сущности базы данных, схема v1 (PLAN.md, §7.3 «Остальные данные»).
// Это «строки таблиц», а не доменные типы: правила игры живут в Gorodki.Domain.

/// <summary>Роль пользователя.</summary>
public enum UserRole : short
{
    Player = 0,
    /// <summary>Демо-аккаунт для показа: публичные события без задержки, повтор забега.</summary>
    Demo = 1,
    Admin = 2,
}

/// <summary>Игрок.</summary>
public sealed class UserEntity
{
    public Guid Id { get; set; }

    /// <summary>Идентификатор пользователя Google (claim <c>sub</c>).</summary>
    public string? GoogleSubject { get; set; }

    /// <summary>Идентификатор «Входа через Apple» (сборка Paid).</summary>
    public string? AppleSubject { get; set; }

    /// <summary>Ник, как его видят другие.</summary>
    public required string DisplayName { get; set; }

    /// <summary>Ник в нижнем регистре — для проверки уникальности без учёта регистра.</summary>
    public required string NormalizedName { get; set; }

    /// <summary>Цвет игрока: номер в палитре из 12 цветов (PLAN.md, §6.5).</summary>
    public short ColorIndex { get; set; }

    public UserRole Role { get; set; }

    /// <summary>Когда подтверждён возраст 16+.</summary>
    public DateTimeOffset? AgeConfirmedAt { get; set; }

    /// <summary>Версия принятого соглашения и когда (закон 99-З).</summary>
    public int? ConsentVersion { get; set; }

    public DateTimeOffset? ConsentedAt { get; set; }

    /// <summary>Отдельное согласие показывать ник, аватар и землю другим; без него — «Игрок #1234».</summary>
    public bool PublicProfile { get; set; }

    /// <summary>Инвайт-код, по которому игрок пришёл.</summary>
    public string? InviteCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Запрос на удаление: данные стираются в течение 15 дней (PLAN.md, §3.16).</summary>
    public DateTimeOffset? DeletionRequestedAt { get; set; }

    /// <summary>Заморозка (PLAN.md, §3.9, слой 5): до этого момента захваты игрока не применяются.</summary>
    public DateTimeOffset? FrozenUntil { get; set; }
}

/// <summary>
/// Refresh-токен. Хранится только SHA-256 хэш; все токены одного входа — одна «семья»:
/// при подозрении на кражу отзывается вся семья (PLAN.md, D10).
/// </summary>
public sealed class RefreshTokenEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid FamilyId { get; set; }

    public required byte[] TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Когда токен заменён или отозван; null — действует.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Какой токен выдан взамен (при обычном обновлении).</summary>
    public Guid? ReplacedById { get; set; }
}

/// <summary>Инвайт-код закрытой регистрации (Сезоны 0–2).</summary>
public sealed class InviteEntity
{
    public required string Code { get; set; }

    public int MaxUses { get; set; }

    public int UsedCount { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Скрытый столбец PostgreSQL <c>xmin</c>: правка приглашения через EF не затрёт чужую. Сама регистрация забирает
    /// приглашение атомарным UPDATE с условием (AuthEndpoints), ей эта версия не нужна.
    /// </summary>
    public uint Version { get; set; }
}

/// <summary>Версия игрового конфига: все числа правил. Забег проверяется той версией, с которой начат.</summary>
public sealed class GameConfigEntity
{
    public int Version { get; set; }

    /// <summary>Сами числа — JSON (хранится как jsonb).</summary>
    public required string Json { get; set; }

    public DateTimeOffset ActiveFrom { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Откуда забег.</summary>
public enum RunSource : short
{
    Live = 0,
    /// <summary>Повтор записанного забега (только роли demo и admin).</summary>
    Replay = 1,
}

public enum RunStatus : short
{
    Active = 0,
    Finished = 1,
    /// <summary>Закрыт сервером: начат более поздний забег, забег дольше 4 часов или брошен. Телефон ещё может завершить его сам.</summary>
    Abandoned = 2,
}

/// <summary>Забег.</summary>
public sealed class RunEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public League League { get; set; }

    public RunSource Source { get; set; }

    public int ConfigVersion { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public RunStatus Status { get; set; }

    /// <summary>До какой точки забег уже обработан (непрерывный префикс); −1 — ещё ни одной.</summary>
    public int ProcessedSeq { get; set; } = -1;

    /// <summary>Когда сервер узнал о забеге (по часам сервера). От этого момента считается окно приёма кусков.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// На сколько часы телефона спешили (плюс) или отставали (минус) при старте забега, мс. Признак для оценки доверия;
    /// сутки, ночь и устаревание считаются по часам сервера с этой поправкой.
    /// </summary>
    public long ClockSkewMs { get; set; }

    /// <summary>Идентификатор установки из Keychain (переживает переустановку приложения).</summary>
    public Guid DeviceId { get; set; }

    public required string AppVersion { get; set; }

    /// <summary>Разрешено ли приложению «Движение»: без него захват невозможен (PLAN.md, §3.9).</summary>
    public bool MotionAuthorized { get; set; }

    /// <summary>Номер последней точки — телефон сообщает при завершении; до этого <c>null</c>.</summary>
    public int? LastSeq { get; set; }

    /// <summary>Сколько кусков принято — для лимита на забег.</summary>
    public int ChunkCount { get; set; }

    /// <summary>Сколько байт кусков хранится — для лимита на забег и на игрока в сутки.</summary>
    public int StoredBytes { get; set; }

    /// <summary>Последний номер непрерывного начала следа (от точки 0 без дыр), −1 — точки 0 ещё нет. Обновляется при приёме кусков.</summary>
    public int PrefixEndSeq { get; set; } = -1;

    /// <summary>
    /// До какого момента (часы телефона, мс) переданы все данные датчиков — только по кускам непрерывного начала:
    /// кусок за дырой не может объявить, что всё отправлено.
    /// </summary>
    public long PrefixSensorsMs { get; set; }

    /// <summary>Первый забег новичка: точность до 35 м вместо 25 (§3.9). Решает сервер при старте, телефон судит так же.</summary>
    public bool Newcomer { get; set; }

    /// <summary>
    /// Когда забег открыл туман (один раз за забег, §7.3) или очистка истории исследований (<c>FogHistory</c>) пометила
    /// забег: fog_new_cells = 0, туман он уже не откроет. null — ещё не открывал.
    /// </summary>
    public DateTimeOffset? FogStampedAt { get; set; }

    /// <summary>Сколько новых клеток тумана открыл забег — для итога «+N га»; 0 и у забега, помеченного очисткой истории.</summary>
    public int? FogNewCells { get; set; }

    /// <summary>Когда засчитаны визиты забега (≥50 м следа внутри своего куска, PLAN.md §3.3) — один раз за забег.</summary>
    public DateTimeOffset? VisitsProcessedAt { get; set; }

    /// <summary>Сколько своих кусков забег освежил визитом.</summary>
    public int? VisitedParcels { get; set; }

    /// <summary>
    /// Засчитанный путь забега, метры (судья отрезков, без обрезки) — пробег для защиты от мультиаккаунтов (§3.3).
    /// Считается вместе с визитами; null — ещё не посчитан.
    /// </summary>
    public double? AcceptedMeters { get; set; }

    /// <summary>
    /// Когда стёрты сырые точки забега (через 14 дней, PLAN.md §3.16): содержимое кусков пустое, номера и время остались.
    /// </summary>
    public DateTimeOffset? PointsPurgedAt { get; set; }

    /// <summary>Сколько раз подряд туман забега не открылся из-за ошибки — для паузы перед повтором.</summary>
    public int FogFailures { get; set; }

    /// <summary>Туман забега, который не открылся, не пробуется раньше этого момента: он не стоит первым в очереди.</summary>
    public DateTimeOffset? FogRetryAt { get; set; }

    /// <summary>Сколько раз подряд визиты забега не посчитались из-за ошибки.</summary>
    public int VisitsFailures { get; set; }

    /// <summary>Визиты забега, которые не посчитались, не пробуются раньше этого момента.</summary>
    public DateTimeOffset? VisitsRetryAt { get; set; }
}

/// <summary>Слой «Исследования»: у пешком и на велосипеде — своя карта тумана (PLAN.md, §3.10).</summary>
public enum FogLayerKind : short
{
    Foot = 1,
    Bike = 2,
}

/// <summary>
/// Тайл тумана игрока (веб-меркатор, уровень 14): какие клетки G22 он открыл. Биты сжаты Deflate.
/// Где человек ходит — личные данные: тайлы видит только он сам, и они удаляются вместе с ним и при очистке истории
/// исследований (<c>FogHistory</c>).
/// </summary>
public sealed class FogTileEntity
{
    public Guid UserId { get; set; }

    public FogLayerKind Layer { get; set; }

    /// <summary><c>SeasonCalendar.AllTime</c> (−1) — за всё время; 0, 1, 2… — сезонный слой (со сменой сезонов).</summary>
    public int Season { get; set; }

    public int TileX { get; set; }

    public int TileY { get; set; }

    public required byte[] Bits { get; set; }

    public int CellCount { get; set; }

    /// <summary>
    /// Растёт при каждом изменении: приложение перезапрашивает только новые версии. Это момент изменения, мс Unix
    /// (<c>FogProcessor.NextVersion</c>), — тайл, открытый заново после очистки истории, не начинает с 1.
    /// </summary>
    public long Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Кусок точек забега, присланный телефоном. Повтор того же содержимого не сохраняется (идемпотентность).</summary>
public sealed class RunChunkEntity
{
    public Guid RunId { get; set; }

    public int FirstSeq { get; set; }

    public int LastSeq { get; set; }

    /// <summary>SHA-256 содержимого.</summary>
    public required byte[] ContentHash { get; set; }

    /// <summary>Точки и датчики в компактном двоичном виде. Хранятся 14 дней, потом массив пустой (<see cref="RunEntity.PointsPurgedAt"/>).</summary>
    public required byte[] Points { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>Время первой и последней точки (часы телефона, мс) — чтобы решать о готовности петли без чтения точек.</summary>
    public long FirstPointMs { get; set; }

    public long LastPointMs { get; set; }

    /// <summary>До какого момента, по словам телефона, отправлены все данные датчиков (мс).</summary>
    public long SensorsCompleteThroughMs { get; set; }
}

/// <summary>
/// Кусок земли: один простой многоугольник внутри одного тайла UTM 1 × 1 км (ADR 0003).
/// Состояние наследуется при разрезании; поля совпадают с <c>Gorodki.Domain.Territory.ParcelState</c>.
/// </summary>
public sealed class ParcelEntity
{
    public long Id { get; set; }

    public League League { get; set; }

    public int TileX { get; set; }

    public int TileY { get; set; }

    public Guid OwnerId { get; set; }

    public short Level { get; set; }

    public DateTimeOffset LastVisitAt { get; set; }

    public DateTimeOffset LastLevelUpAt { get; set; }

    public DateTimeOffset? ShieldUntil { get; set; }

    public DateTimeOffset? SiegeUntil { get; set; }

    /// <summary>Окно лимита снятия уровней и кто уже снял уровень (<c>ParcelState</c>).</summary>
    public DateTimeOffset? LossWindowSince { get; set; }

    public Guid[] LossAttackers { get; set; } = [];

    /// <summary>Многоугольник в UTM 34N (EPSG:32634), вершины на сетке 0,1 м.</summary>
    public required Polygon Geometry { get; set; }
}

/// <summary>Сезон (PLAN.md, §3.4): номер как в плане, начало — полночь по Минску (хранится в UTC).</summary>
public sealed class SeasonEntity
{
    public int Number { get; set; }

    public required string Name { get; set; }

    public DateTimeOffset StartsAt { get; set; }
}

/// <summary>
/// Журнал захвата по одному тайлу: след — где земля после захвата стала другой (PLAN.md, §7.3, шаг B.5).
/// Пишется в той же транзакции, что и земля, хранится 7 дней — столько доступен откат (§3.9, слой 5).
/// </summary>
public sealed class CaptureJournalEntity
{
    public Guid CaptureId { get; set; }

    public int TileX { get; set; }

    public int TileY { get; set; }

    public League League { get; set; }

    public DateTimeOffset AppliedAt { get; set; }

    /// <summary>След в TWKB (сетка 0,1 м, без потерь): многоугольник или мультимногоугольник.</summary>
    public required byte[] Footprint { get; set; }
}

/// <summary>
/// Земля одного состояния внутри следа — до или после захвата. Без внешнего ключа на владельца: журнал переживает
/// удаление аккаунта, а откат вернёт землю несуществующего игрока ничьей.
/// </summary>
public sealed class CaptureJournalPieceEntity
{
    public long Id { get; set; }

    public Guid CaptureId { get; set; }

    public int TileX { get; set; }

    public int TileY { get; set; }

    /// <summary><c>false</c> — земля до захвата, <c>true</c> — после.</summary>
    public bool After { get; set; }

    public Guid OwnerId { get; set; }

    public short Level { get; set; }

    public DateTimeOffset LastVisitAt { get; set; }

    public DateTimeOffset LastLevelUpAt { get; set; }

    public DateTimeOffset? ShieldUntil { get; set; }

    public DateTimeOffset? SiegeUntil { get; set; }

    public DateTimeOffset? LossWindowSince { get; set; }

    public Guid[] LossAttackers { get; set; } = [];

    /// <summary>Геометрия в TWKB (сетка 0,1 м).</summary>
    public required byte[] Geometry { get; set; }
}

public enum CaptureStatus : short
{
    /// <summary>Ждёт точек, данных датчиков или своей очереди. Ноль — чтобы забытый статус не означал «применено».</summary>
    Pending = 0,
    Applied = 1,
    Rejected = 2,
    /// <summary>Петля слишком старая: от неё до получения всего нужного прошло больше 3 часов (PLAN.md, §7.3).</summary>
    Stale = 3,
    /// <summary>Движок не смог применить петлю и после повтора — земля не изменилась, ошибка записана.</summary>
    Failed = 4,
}

/// <summary>Как замкнулась петля на телефоне. Имена совпадают с <c>LoopClosure</c> в GameCore.</summary>
public enum LoopClosure : short
{
    /// <summary>След пересёк сам себя.</summary>
    Crossing = 0,
    /// <summary>След вернулся ближе R к прежней точке.</summary>
    Proximity = 1,
}

/// <summary>Заявка петли и её итог (PLAN.md, §3.2, §7.3).</summary>
public sealed class CaptureEntity
{
    /// <summary>UUIDv5 от (забег, номер последней точки): повторная заявка не применяется дважды.</summary>
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public Guid UserId { get; set; }

    public League League { get; set; }

    /// <summary>Номер заявки в забеге (0, 1, 2…): заявки забега обрабатываются по порядку.</summary>
    public int ClaimNo { get; set; }

    public int StartSeq { get; set; }

    public int EndSeq { get; set; }

    public LoopClosure Closure { get; set; }

    /// <summary>Грубая площадь на телефоне, м² — для сравнения с точной.</summary>
    public double EstimatedArea { get; set; }

    public CaptureStatus Status { get; set; }

    /// <summary>Причина отказа — стабильный код для приложения (<c>too_small</c>, <c>segment_broken:vehicle</c>…).</summary>
    public string? RejectCode { get; set; }

    /// <summary>Сколько земли взято, м² (у применённых).</summary>
    public double AreaSquareMeters { get; set; }

    /// <summary>Площадь по видам последствий, JSON <c>{"claimedNeutral": 1234.5, …}</c>.</summary>
    public string? AreaByOutcome { get; set; }

    /// <summary>Изменённые тайлы, JSON <c>[[x, y], …]</c>: их версии выросли, приложение их перезапросит.</summary>
    public string? ChangedTiles { get; set; }

    /// <summary>Контур P из шага A — только у применённых (у отказов бывает до 3,5 км² и тысяч вершин).</summary>
    public Geometry? Shape { get; set; }

    /// <summary>Когда заявка пришла на сервер.</summary>
    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>Время петли по часам сервера: конец петли с поправкой на сдвиг часов телефона, но не позже прихода заявки.</summary>
    public DateTimeOffset? EffectiveAt { get; set; }

    /// <summary>Когда у сервера появилось всё нужное для решения — по нему считается «старше 3 часов».</summary>
    public DateTimeOffset? EvidenceAt { get; set; }

    public int Attempts { get; set; }

    /// <summary>Аренда обработчика: до этого момента заявку обрабатывает владелец <see cref="LeaseToken"/>.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    public Guid? LeaseToken { get; set; }

    /// <summary>Порядковый номер применения к карте (последовательность в базе): по нему карту можно переиграть.</summary>
    public long? AppliedSeq { get; set; }

    public DateTimeOffset? AppliedAt { get; set; }

    /// <summary>Версия конфига, правилами земли которой применена петля (карта общая — правила на момент применения).</summary>
    public int? TerritoryConfigVersion { get; set; }

    /// <summary>Последняя ошибка обработки — для разбора.</summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Когда захват откачен (PLAN.md, §3.9, слой 5). Статус остаётся «применён»: захват был, его земля возвращена
    /// прежним хозяевам; для суточного лимита он по-прежнему считается.
    /// </summary>
    public DateTimeOffset? RolledBackAt { get; set; }

    /// <summary>Сколько земли вернул откат, м² (остальное после захвата уже изменили — оно не тронуто).</summary>
    public double? RolledBackArea { get; set; }

    /// <summary>Каким заданием откачен.</summary>
    public Guid? RollbackId { get; set; }
}

public enum CaptureRollbackStatus : short
{
    Pending = 0,
    Done = 1,
}

/// <summary>
/// Задание «откатить захваты игрока» и его итог — заодно журнал решений: кто, кого, почему (PLAN.md, §3.9, слой 5).
/// Выполняет фоновый обработчик захватов — движок участков работает в одном потоке (ADR 0003).
/// </summary>
public sealed class CaptureRollbackEntity
{
    public Guid Id { get; set; }

    /// <summary>Чьи захваты откатываются.</summary>
    public Guid UserId { get; set; }

    /// <summary>Кто решил (администратор).</summary>
    public Guid RequestedBy { get; set; }

    public required string Reason { get; set; }

    public DateTimeOffset RequestedAt { get; set; }

    public CaptureRollbackStatus Status { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>Захватов откачено.</summary>
    public int RolledBack { get; set; }

    /// <summary>Захватов без журнала (старше недели или применены до журнала) — не откачены.</summary>
    public int WithoutJournal { get; set; }

    /// <summary>Захватов, которые движок не смог откатить (ошибка самопроверки или постоянные конфликты).</summary>
    public int Failed { get; set; }

    /// <summary>Сколько земли возвращено прежним хозяевам, м².</summary>
    public double RestoredArea { get; set; }

    /// <summary>Земля в следах, которую после захватов уже изменили, — не тронута, м².</summary>
    public double SkippedArea { get; set; }

    public string? LastError { get; set; }
}

/// <summary>Версия тайла: растёт при каждом изменении земли в нём. Клиенты перезапрашивают только изменившиеся тайлы.</summary>
public sealed class TileVersionEntity
{
    public League League { get; set; }

    public int TileX { get; set; }

    public int TileY { get; set; }

    public long Version { get; set; }
}

/// <summary>
/// Приватная зона игрока (PLAN.md, §3.16): круг вокруг точки, где визиты не засчитываются. Радиус — из игрового конфига.
/// Видит только сам игрок; удаляется вместе с аккаунтом и входит в «мои данные».
/// </summary>
public sealed class PrivacyZoneEntity
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Какой рейтинг.</summary>
public enum LeaderboardBoard : short
{
    /// <summary>«Кто открыл больше» (§3.10): открытая площадь тумана, м².</summary>
    Exploration = 1,
}

/// <summary>Слой рейтинга «Исследования»: совпадает с <see cref="FogLayerKind"/>, плюс «Всего» — их сумма.</summary>
public enum LeaderboardLayer : short
{
    Total = 0,
    Foot = 1,
    Bike = 2,
}

/// <summary>
/// Строка ежедневного среза рейтинга (PLAN.md, §3.5: «рейтинги — по ежедневному снимку»): игрок, его значение и место
/// в сутки по Минску. Хранится неделю; удаляется вместе с аккаунтом.
/// </summary>
public sealed class LeaderboardSnapshotEntity
{
    public DateOnly Day { get; set; }

    public LeaderboardBoard Board { get; set; }

    public LeaderboardLayer Layer { get; set; }

    /// <summary>Номер сезона; −1 — «за всё время».</summary>
    public int Season { get; set; }

    public Guid UserId { get; set; }

    /// <summary>Значение: для «Исследования» — открытая площадь, м².</summary>
    public double Value { get; set; }

    /// <summary>Место: одинаковое значение — одинаковое место (1, 2, 2, 4).</summary>
    public int Rank { get; set; }
}
