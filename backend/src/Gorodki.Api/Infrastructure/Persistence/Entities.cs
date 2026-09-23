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
}

/// <summary>Кусок точек забега, присланный телефоном. Повтор того же содержимого не сохраняется (идемпотентность).</summary>
public sealed class RunChunkEntity
{
    public Guid RunId { get; set; }

    public int FirstSeq { get; set; }

    public int LastSeq { get; set; }

    /// <summary>SHA-256 содержимого.</summary>
    public required byte[] ContentHash { get; set; }

    /// <summary>Точки в компактном двоичном виде. Сырые точки хранятся 14 дней.</summary>
    public required byte[] Points { get; set; }

    public DateTimeOffset ReceivedAt { get; set; }
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

    public DateTimeOffset CapturedAt { get; set; }

    /// <summary>Многоугольник в UTM 34N (EPSG:32634), вершины на сетке 0,1 м.</summary>
    public required Polygon Geometry { get; set; }
}

public enum CaptureStatus : short
{
    Applied = 0,
    Rejected = 1,
    /// <summary>Пришёл позже 3 часов после петли — не применяется.</summary>
    Stale = 2,
}

/// <summary>Заявка петли и её итог.</summary>
public sealed class CaptureEntity
{
    /// <summary>UUIDv5 от (забег, номер последней точки): повторная заявка не применяется дважды.</summary>
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public Guid UserId { get; set; }

    public League League { get; set; }

    public int StartSeq { get; set; }

    public int EndSeq { get; set; }

    public CaptureStatus Status { get; set; }

    /// <summary>Причина отказа (<c>CaptureRejection</c> из шага A) — показывается игроку.</summary>
    public short? RejectReason { get; set; }

    public double AreaSquareMeters { get; set; }

    /// <summary>Контур P из шага A (может быть мультимногоугольником).</summary>
    public Geometry? Shape { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Версия тайла: растёт при каждом изменении земли в нём. Клиенты перезапрашивают только изменившиеся тайлы.</summary>
public sealed class TileVersionEntity
{
    public League League { get; set; }

    public int TileX { get; set; }

    public int TileY { get; set; }

    public long Version { get; set; }
}
