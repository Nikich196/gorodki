using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Gorodki.Domain.Runs;

/// <summary>
/// Двоичный формат куска забега (столбец <c>run_chunks.points</c>), версия 1. Все числа little-endian:
/// <code>
/// byte   версия формата = 1
/// int32  номер первой точки
/// int64  до какого момента переданы все данные датчиков, мс
/// int32  число точек, затем на точку 21 байт:
///        int64 время, мс · int32 широта·1e7 · int32 долгота·1e7 · uint16 точность, дм · uint16 скорость, см/с (65535 — нет) · byte признаки
/// int32  число записей движения, затем на запись 9 байт: int64 время, мс · byte вид движения
/// int32  число записей шагомера, затем на запись 20 байт: int64 начало, мс · int64 конец, мс · int32 шаги (−1 — неизвестно)
/// </code>
/// Номер первой точки входит в данные, поэтому одинаковые точки на разных местах забега дают разный хэш.
/// </summary>
public static class TrackChunkCodec
{
    public const byte FormatVersion = 1;

    private const int HeaderSize = 1 + 4 + 8;
    private const int PointSize = 8 + 4 + 4 + 2 + 2 + 1;
    private const int MotionSize = 8 + 1;
    private const int StepSize = 8 + 8 + 4;

    public static byte[] Encode(TrackChunk chunk)
    {
        for (var i = 0; i < chunk.Points.Count; i++)
        {
            if (chunk.Points[i].Seq != chunk.FirstSeq + i)
            {
                throw new ArgumentException($"Точки куска должны идти подряд с номера {chunk.FirstSeq}: на месте {i} стоит {chunk.Points[i].Seq}.");
            }
        }

        var size = HeaderSize
            + 4 + (chunk.Points.Count * PointSize)
            + 4 + (chunk.Motion.Count * MotionSize)
            + 4 + (chunk.Steps.Count * StepSize);
        var buffer = new byte[size];
        var span = buffer.AsSpan();

        span[0] = FormatVersion;
        BinaryPrimitives.WriteInt32LittleEndian(span[1..], chunk.FirstSeq);
        BinaryPrimitives.WriteInt64LittleEndian(span[5..], chunk.SensorsCompleteThroughMs);
        var offset = HeaderSize;

        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], chunk.Points.Count);
        offset += 4;
        foreach (var point in chunk.Points)
        {
            BinaryPrimitives.WriteInt64LittleEndian(span[offset..], point.TimeMs);
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 8)..], point.LatitudeE7);
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 12)..], point.LongitudeE7);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 16)..], point.AccuracyDecimeters);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(offset + 18)..], point.SpeedCentimetersPerSecond);
            span[offset + 20] = (byte)point.Flags;
            offset += PointSize;
        }

        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], chunk.Motion.Count);
        offset += 4;
        foreach (var sample in chunk.Motion)
        {
            BinaryPrimitives.WriteInt64LittleEndian(span[offset..], sample.TimeMs);
            span[offset + 8] = (byte)sample.Activity;
            offset += MotionSize;
        }

        BinaryPrimitives.WriteInt32LittleEndian(span[offset..], chunk.Steps.Count);
        offset += 4;
        foreach (var sample in chunk.Steps)
        {
            BinaryPrimitives.WriteInt64LittleEndian(span[offset..], sample.StartMs);
            BinaryPrimitives.WriteInt64LittleEndian(span[(offset + 8)..], sample.EndMs);
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 16)..], sample.Steps ?? -1);
            offset += StepSize;
        }

        return buffer;
    }

    /// <exception cref="FormatException">Данные повреждены или записаны неизвестной версией формата.</exception>
    public static TrackChunk Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || data[0] != FormatVersion)
        {
            throw new FormatException("Неизвестная версия формата куска забега.");
        }

        var firstSeq = BinaryPrimitives.ReadInt32LittleEndian(data[1..]);
        var sensorsCompleteThroughMs = BinaryPrimitives.ReadInt64LittleEndian(data[5..]);
        var offset = HeaderSize;

        var pointCount = ReadCount(data, ref offset, PointSize);
        var points = new TrackPoint[pointCount];
        for (var i = 0; i < pointCount; i++)
        {
            points[i] = TrackPoint.FromStored(
                firstSeq + i,
                BinaryPrimitives.ReadInt64LittleEndian(data[offset..]),
                BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 8)..]),
                BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 12)..]),
                BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 16)..]),
                BinaryPrimitives.ReadUInt16LittleEndian(data[(offset + 18)..]),
                (PointFlags)data[offset + 20]);
            offset += PointSize;
        }

        var motionCount = ReadCount(data, ref offset, MotionSize);
        var motion = new MotionSample[motionCount];
        for (var i = 0; i < motionCount; i++)
        {
            motion[i] = new MotionSample(BinaryPrimitives.ReadInt64LittleEndian(data[offset..]), (MotionActivity)data[offset + 8]);
            offset += MotionSize;
        }

        var stepCount = ReadCount(data, ref offset, StepSize);
        var steps = new StepSample[stepCount];
        for (var i = 0; i < stepCount; i++)
        {
            var count = BinaryPrimitives.ReadInt32LittleEndian(data[(offset + 16)..]);
            steps[i] = new StepSample(
                BinaryPrimitives.ReadInt64LittleEndian(data[offset..]),
                BinaryPrimitives.ReadInt64LittleEndian(data[(offset + 8)..]),
                count < 0 ? null : count);
            offset += StepSize;
        }

        if (offset != data.Length)
        {
            throw new FormatException("После данных куска забега остались лишние байты.");
        }

        return new TrackChunk(firstSeq, sensorsCompleteThroughMs, points, motion, steps);
    }

    /// <summary>SHA-256 записанного куска: по нему повтор того же куска узнаётся без сравнения точек.</summary>
    public static byte[] Hash(ReadOnlySpan<byte> encoded) => SHA256.HashData(encoded);

    /// <summary>Читает число записей и заранее проверяет, что записи целиком помещаются в оставшиеся данные.</summary>
    private static int ReadCount(ReadOnlySpan<byte> data, ref int offset, int recordSize)
    {
        if (data.Length - offset < 4)
        {
            throw new FormatException("Кусок забега обрезан.");
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(data[offset..]);
        offset += 4;
        if (count < 0 || (long)count * recordSize > data.Length - offset)
        {
            throw new FormatException("Кусок забега обрезан или повреждён.");
        }

        return count;
    }
}
