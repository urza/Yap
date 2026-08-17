using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Yap.Services;

/// <summary>
/// Reads IP2Location LITE DB3 BIN files for IP-to-country/region/city lookup.
/// No NuGet dependency — parses the binary format directly.
/// Drop a DB3 BIN file into Data/ip2location/ to enable.
/// The DB3.IPV6 variant holds both address families; with the IPv4-only file, IPv6 lookups return null.
/// </summary>
public class GeoLocationService
{
    private readonly byte[]? _data;
    private readonly uint _ipv4Count;
    private readonly uint _ipv4BaseAddr;
    private readonly uint _ipv4IndexBaseAddr;
    private readonly uint _ipv6Count;
    private readonly uint _ipv6BaseAddr;
    private readonly uint _ipv6IndexBaseAddr;
    private readonly int _columnCount;
    private readonly int _rowSize;
    private readonly int _rowSizeV6;
    private readonly ILogger<GeoLocationService> _logger;

    public bool IsAvailable => _data != null;

    public GeoLocationService(IWebHostEnvironment env, ILogger<GeoLocationService> logger)
    {
        _logger = logger;

        try
        {
            var dir = Path.Combine(env.ContentRootPath, "Data", "ip2location");
            if (!Directory.Exists(dir))
            {
                _logger.LogInformation("IP2Location: directory not found, geolocation disabled");
                return;
            }

            // Find any DB3 BIN file; the IPV6 variant is a superset (both families), so prefer it when both are present
            var binFile = Directory.GetFiles(dir, "*DB3*.BIN")
                .OrderByDescending(f => f.Contains("IPV6", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (binFile == null)
            {
                _logger.LogInformation("IP2Location: no *DB3*.BIN file found, geolocation disabled");
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _data = File.ReadAllBytes(binFile);
            sw.Stop();

            // Header: byte[0]=dbType, byte[1]=columns, bytes[5..8]=ipv4Count, bytes[9..12]=ipv4Base,
            // bytes[13..16]=ipv6Count, bytes[17..20]=ipv6Base, bytes[21..24]=ipv4IndexBase, bytes[25..28]=ipv6IndexBase
            _columnCount = _data[1];
            _rowSize = _columnCount * 4;
            _ipv4Count = ReadUInt32(5);
            _ipv4BaseAddr = ReadUInt32(9);
            _ipv6Count = ReadUInt32(13);
            _ipv6BaseAddr = ReadUInt32(17);
            _ipv4IndexBaseAddr = ReadUInt32(21);
            _ipv6IndexBaseAddr = ReadUInt32(25);
            // IPv6 rows carry a 16-byte key instead of 4; the remaining columns stay 4-byte pointers
            _rowSizeV6 = 16 + (_columnCount - 1) * 4;

            _logger.LogInformation("IP2Location DB{Type} loaded: {V4Count} IPv4 + {V6Count} IPv6 records from {File} ({Size}MB, {Time}ms)",
                _data[0], _ipv4Count, _ipv6Count, Path.GetFileName(binFile),
                _data.Length / (1024 * 1024), sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load IP2Location database, geolocation disabled");
            _data = null;
        }
    }

    public GeoInfo? Lookup(string? ipAddress)
    {
        if (_data == null || string.IsNullOrEmpty(ipAddress)) return null;

        try
        {
            if (!IPAddress.TryParse(ipAddress, out var addr))
                return null;

            // Dualstack Kestrel reports IPv4 clients as ::ffff:a.b.c.d — unwrap them to the IPv4 table
            if (addr.IsIPv4MappedToIPv6)
                addr = addr.MapToIPv4();

            if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var bytes = addr.GetAddressBytes();
                uint ipNum = (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
                return LookupIPv4(ipNum);
            }

            return LookupIPv6(addr);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GeoLocation lookup failed for {IP}", ipAddress);
            return null;
        }
    }

    private GeoInfo? LookupIPv4(uint ipNum)
    {
        uint low = 0;
        uint high = _ipv4Count - 1;

        // Use index for faster narrowing (index by first two octets)
        if (_ipv4IndexBaseAddr > 0)
        {
            var indexPos = _ipv4IndexBaseAddr - 1 + ((ipNum >> 16) * 8);
            low = ReadUInt32((int)indexPos);
            high = ReadUInt32((int)indexPos + 4);
        }

        // Binary search
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var rowOffset = (int)(_ipv4BaseAddr - 1 + mid * (uint)_rowSize);
            var ipFrom = ReadUInt32(rowOffset);
            var ipTo = ReadUInt32(rowOffset + _rowSize); // ip_from of next row

            if (ipNum < ipFrom)
            {
                if (mid == 0) break;
                high = mid - 1;
            }
            else if (ipNum >= ipTo)
            {
                low = mid + 1;
            }
            else
            {
                return ReadRecord(rowOffset + 4); // pointers follow the 4-byte key
            }
        }

        return null;
    }

    private GeoInfo? LookupIPv6(IPAddress addr)
    {
        if (_ipv6Count == 0) return null; // IPv4-only BIN file loaded

        var bytes = addr.GetAddressBytes();
        var ipNum = BinaryPrimitives.ReadUInt128BigEndian(bytes);

        uint low = 0;
        uint high = _ipv6Count - 1;

        // Use index for faster narrowing (index by first two address bytes, same layout as IPv4's)
        if (_ipv6IndexBaseAddr > 0)
        {
            var indexPos = _ipv6IndexBaseAddr - 1 + (uint)(((bytes[0] << 8) | bytes[1]) * 8);
            low = ReadUInt32((int)indexPos);
            high = ReadUInt32((int)indexPos + 4);
        }

        // Binary search — same shape as LookupIPv4, with a 128-bit key
        while (low <= high)
        {
            var mid = (low + high) / 2;
            var rowOffset = (int)(_ipv6BaseAddr - 1 + mid * (uint)_rowSizeV6);
            var ipFrom = ReadUInt128(rowOffset);
            var ipTo = ReadUInt128(rowOffset + _rowSizeV6); // ip_from of next row

            if (ipNum < ipFrom)
            {
                if (mid == 0) break;
                high = mid - 1;
            }
            else if (ipNum >= ipTo)
            {
                low = mid + 1;
            }
            else
            {
                return ReadRecord(rowOffset + 16); // pointers follow the 16-byte key
            }
        }

        return null;
    }

    private GeoInfo ReadRecord(int pointerBase)
    {
        // DB3 pointer columns after the row key: [country_ptr:4][region_ptr:4][city_ptr:4]
        var countryPtr = (int)ReadUInt32(pointerBase);
        var regionPtr = (int)ReadUInt32(pointerBase + 4);
        var cityPtr = (int)ReadUInt32(pointerBase + 8);

        var countryCode = ReadString(countryPtr);
        var countryName = ReadString(countryPtr + 3);
        var region = ReadString(regionPtr);
        var city = ReadString(cityPtr);

        return new GeoInfo
        {
            CountryCode = countryCode,
            Country = countryName,
            Region = region,
            City = city
        };
    }

    private string ReadString(int pos)
    {
        if (pos < 0 || pos >= _data!.Length) return "";
        var length = _data[pos];
        if (pos + 1 + length > _data.Length) return "";
        return Encoding.UTF8.GetString(_data, pos + 1, length);
    }

    private uint ReadUInt32(int offset)
    {
        return BitConverter.ToUInt32(_data!, offset);
    }

    // The BIN file stores every integer little-endian, including the 16-byte IPv6 key —
    // only the wire address bytes (GetAddressBytes) are big-endian. Don't "fix" one to match the other.
    private UInt128 ReadUInt128(int offset)
    {
        return BinaryPrimitives.ReadUInt128LittleEndian(_data.AsSpan(offset));
    }
}

public class GeoInfo
{
    public string CountryCode { get; init; } = "";
    public string Country { get; init; } = "";
    public string Region { get; init; } = "";
    public string City { get; init; } = "";

    public override string ToString()
    {
        if (CountryCode == "-") return "";
        return Country;
    }
}
