using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Gorodki.Domain.Runs;

/// <summary>
/// Идентификатор заявки петли (PLAN.md, §3.2): <c>capture_id = UUIDv5(забег, номер последней точки)</c>.
/// Повтор заявки даёт тот же идентификатор — сервер не применит петлю дважды. Телефон может посчитать его сам
/// (contracts/README.md): байты — в порядке RFC 4122, старший первым.
/// </summary>
public static class CaptureIds
{
    /// <summary>Пространство имён UUIDv5 заявок петель «Городков». Не менять: от него зависят все идентификаторы.</summary>
    public static readonly Guid Namespace = new("4f2c8a1e-9b3d-4c6f-a1e2-7d5b9c0f3e81");

    /// <summary>UUIDv5 от 16 байт забега и 4 байт номера последней точки (оба — старший байт первым).</summary>
    public static Guid For(Guid runId, int endSeq)
    {
        Span<byte> name = stackalloc byte[20];
        runId.TryWriteBytes(name[..16], bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(name[16..], endSeq);
        return V5(Namespace, name);
    }

    /// <summary>UUID версии 5 по RFC 4122: SHA-1 от пространства имён и имени. SHA-1 здесь требует сам стандарт, это не защита.</summary>
    public static Guid V5(Guid nameSpace, ReadOnlySpan<byte> name)
    {
        var data = new byte[16 + name.Length];
        nameSpace.TryWriteBytes(data.AsSpan(0, 16), bigEndian: true, out _);
        name.CopyTo(data.AsSpan(16));
        var hash = SHA1.HashData(data);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
