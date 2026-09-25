using NetTopologySuite.Geometries;

namespace Gorodki.Domain.Geo;

/// <summary>
/// TWKB («крошечный WKB», https://github.com/TWKB/Specification) с одним знаком после запятой — ровно сетка 0,1 м
/// (<see cref="GeoOps.Grid"/>): координаты на сетке хранятся без потерь, 2–3 байта на вершину вместо 16 у WKB.
/// Совместим с PostGIS <c>ST_AsTWKB(g, 1)</c> и <c>ST_GeomFromTWKB</c> (проверено интеграционным тестом).
/// Нужен журналу захватов: 7 дней прежних кусков должны уместиться в бесплатные 500 МБ базы (PLAN.md, §7.3).
/// </summary>
/// <remarks>
/// Поддерживаются многоугольники и мультимногоугольники в 2D. Все координаты — разности с предыдущей вершиной
/// (через границы колец и многоугольников), в зигзаг-кодировке и переменной длине.
/// </remarks>
public static class Twkb
{
    private const int Precision = 1;
    private const byte PolygonType = 3;
    private const byte MultiPolygonType = 6;
    private const byte BboxFlag = 0x01;
    private const byte SizeFlag = 0x02;
    private const byte IdListFlag = 0x04;
    private const byte ExtendedPrecisionFlag = 0x08;
    private const byte EmptyFlag = 0x10;

    /// <summary>Кодирует (мульти)многоугольник. Вершины обязаны лежать на сетке 0,1 м — иначе запись была бы с потерями.</summary>
    public static byte[] Write(Geometry geometry)
    {
        var type = geometry switch
        {
            Polygon => PolygonType,
            MultiPolygon => MultiPolygonType,
            _ => throw new ArgumentException($"TWKB журнала хранит только многоугольники, а не {geometry.GeometryType}.", nameof(geometry)),
        };
        if (!GeoOps.IsOnGrid(geometry))
        {
            throw new ArgumentException("Вершины не на сетке 0,1 м: TWKB с точностью 0,1 м записал бы их с потерями.", nameof(geometry));
        }

        var buffer = new List<byte> { (byte)(type | (ZigZag(Precision) << 4)) };
        if (geometry.IsEmpty)
        {
            buffer.Add(EmptyFlag);
            return [.. buffer];
        }

        buffer.Add(0);
        long lastX = 0;
        long lastY = 0;

        void WriteRing(LineString ring)
        {
            WriteVarint(buffer, (ulong)ring.NumPoints);
            foreach (var coordinate in ring.Coordinates)
            {
                var x = Scaled(coordinate.X);
                var y = Scaled(coordinate.Y);
                WriteVarint(buffer, ZigZag(x - lastX));
                WriteVarint(buffer, ZigZag(y - lastY));
                lastX = x;
                lastY = y;
            }
        }

        void WritePolygon(Polygon polygon)
        {
            WriteVarint(buffer, (ulong)(1 + polygon.NumInteriorRings));
            WriteRing(polygon.ExteriorRing);
            foreach (var hole in polygon.InteriorRings)
            {
                WriteRing(hole);
            }
        }

        if (geometry is Polygon single)
        {
            WritePolygon(single);
        }
        else
        {
            WriteVarint(buffer, (ulong)geometry.NumGeometries);
            for (var i = 0; i < geometry.NumGeometries; i++)
            {
                WritePolygon((Polygon)geometry.GetGeometryN(i));
            }
        }

        return [.. buffer];
    }

    /// <summary>Читает (мульти)многоугольник в систему координат сервера (UTM 34N, сетка 0,1 м).</summary>
    /// <exception cref="FormatException">
    /// Не TWKB многоугольника, обрезанные данные или многоугольник из них не собрать. Других исключений нет: запись журнала
    /// может быть испорчена как угодно (ручная правка базы), а вызывающим — публичной проекции и откату — нужен один признак
    /// «запись испорчена», иначе испорченная запись роняет чтение карты (docs/architecture/territory-map.md).
    /// </exception>
    public static Geometry Read(byte[] data)
    {
        try
        {
            return ReadGeometry(data);
        }
        catch (ArgumentException e)
        {
            // Байты разобрались, а многоугольник из них не собрать: кольцо короче трёх точек после замыкания, дыры без
            // оболочки — NTS бросает ArgumentException. Для вызывающих это та же испорченная запись.
            throw new FormatException($"TWKB: из данных не собрать многоугольник ({e.Message})", e);
        }
    }

    private static Geometry ReadGeometry(byte[] data)
    {
        var position = 0;
        byte ReadByte() => position < data.Length ? data[position++] : throw new FormatException("TWKB обрезан.");

        ulong ReadVarint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var b = ReadByte();
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return value;
                }
            }

            throw new FormatException("TWKB: слишком длинное число.");
        }

        // Число колец, вершин или многоугольников — не больше, чем их уместится в оставшихся байтах (у каждого хотя бы
        // bytesEach байт). В испорченной записи оно может быть любым: список на миллиард вершин выделялся бы раньше, чем
        // кончатся данные, а число больше int — OverflowException вместо FormatException.
        int ReadCount(int bytesEach)
        {
            var count = ReadVarint();
            return count <= (ulong)((data.Length - position) / bytesEach) ? (int)count : throw new FormatException("TWKB обрезан.");
        }

        var header = ReadByte();
        var type = header & 0x0F;
        var scale = Math.Pow(10, UnZigZag((ulong)(header >> 4)));
        var metadata = ReadByte();
        if ((metadata & ExtendedPrecisionFlag) != 0)
        {
            throw new FormatException("TWKB с высотой или мерой не поддерживается.");
        }

        if (type is not (PolygonType or MultiPolygonType))
        {
            throw new FormatException($"TWKB: ожидался многоугольник, а тип {type}.");
        }

        if ((metadata & EmptyFlag) != 0)
        {
            return type == PolygonType ? GeoOps.EmptyPolygon() : GeoOps.Factory.CreateMultiPolygon();
        }

        if ((metadata & SizeFlag) != 0)
        {
            ReadVarint();
        }

        if ((metadata & BboxFlag) != 0)
        {
            for (var i = 0; i < 4; i++)
            {
                ReadVarint();
            }
        }

        long lastX = 0;
        long lastY = 0;

        LinearRing ReadRing()
        {
            var count = ReadCount(bytesEach: 2); // вершина — две разности, в каждой хотя бы байт
            var coordinates = new List<Coordinate>(count + 1);
            for (var i = 0; i < count; i++)
            {
                lastX += UnZigZag(ReadVarint());
                lastY += UnZigZag(ReadVarint());
                coordinates.Add(new Coordinate(lastX / scale, lastY / scale));
            }

            if (coordinates.Count > 0 && !coordinates[0].Equals2D(coordinates[^1]))
            {
                coordinates.Add(coordinates[0].Copy()); // кольцо без замыкающей точки — замыкаем
            }

            return GeoOps.Factory.CreateLinearRing([.. coordinates]);
        }

        Polygon ReadPolygon()
        {
            var rings = ReadCount(bytesEach: 1); // у кольца хотя бы число вершин
            if (rings == 0)
            {
                return GeoOps.EmptyPolygon();
            }

            var shell = ReadRing();
            var holes = new LinearRing[rings - 1];
            for (var i = 0; i < holes.Length; i++)
            {
                holes[i] = ReadRing();
            }

            return GeoOps.Factory.CreatePolygon(shell, holes);
        }

        if (type == PolygonType)
        {
            return ReadPolygon();
        }

        var polygons = new Polygon[ReadCount(bytesEach: 1)]; // у многоугольника хотя бы число колец
        if ((metadata & IdListFlag) != 0)
        {
            for (var i = 0; i < polygons.Length; i++)
            {
                ReadVarint();
            }
        }

        for (var i = 0; i < polygons.Length; i++)
        {
            polygons[i] = ReadPolygon();
        }

        return GeoOps.Factory.CreateMultiPolygon(polygons);
    }

    private static long Scaled(double value) => (long)Math.Round(value * 10, MidpointRounding.AwayFromZero);

    private static ulong ZigZag(long value) => (ulong)((value << 1) ^ (value >> 63));

    private static long UnZigZag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    private static void WriteVarint(List<byte> buffer, ulong value)
    {
        while (value >= 0x80)
        {
            buffer.Add((byte)(value | 0x80));
            value >>= 7;
        }

        buffer.Add((byte)value);
    }
}
