using System.Net;
using System.Text;

namespace Yap.Services;

/// <summary>
/// Reads IP2Location LITE DB3 BIN files for IP-to-country/region/city lookup.
/// No NuGet dependency — parses the binary format directly.
/// Drop a DB3 BIN file into Data/ip2location/ to enable.
/// </summary>
public class GeoLocationService
{
    private readonly byte[]? _data;
    private readonly uint _ipv4Count;
    private readonly uint _ipv4BaseAddr;
    private readonly uint _ipv4IndexBaseAddr;
    private readonly int _columnCount;
    private readonly int _rowSize;
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
                _logger.LogDebug("IP2Location directory not found, geolocation disabled");
                return;
            }

            // Find any DB3 BIN file
            var binFile = Directory.GetFiles(dir, "*DB3*.BIN").FirstOrDefault();
            if (binFile == null)
            {
                _logger.LogDebug("No DB3 BIN file found in Data/ip2location/, geolocation disabled");
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _data = File.ReadAllBytes(binFile);
            sw.Stop();

            // Header: byte[0]=dbType, byte[1]=columns, bytes[5..8]=ipv4Count, bytes[9..12]=ipv4Base, bytes[21..24]=ipv4IndexBase
            _columnCount = _data[1];
            _rowSize = _columnCount * 4;
            _ipv4Count = ReadUInt32(5);
            _ipv4BaseAddr = ReadUInt32(9);
            _ipv4IndexBaseAddr = ReadUInt32(21);

            _logger.LogInformation("IP2Location DB{Type} loaded: {Count} records from {File} ({Size}MB, {Time}ms)",
                _data[0], _ipv4Count, Path.GetFileName(binFile),
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

            // IPv4 only for now
            if (addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return null;

            var bytes = addr.GetAddressBytes();
            uint ipNum = (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];

            return LookupIPv4(ipNum);
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
                return ReadRecord(rowOffset);
            }
        }

        return null;
    }

    private GeoInfo ReadRecord(int rowOffset)
    {
        // DB3 row: [ip_from:4][country_ptr:4][region_ptr:4][city_ptr:4]
        var countryPtr = (int)ReadUInt32(rowOffset + 4);
        var regionPtr = (int)ReadUInt32(rowOffset + 8);
        var cityPtr = (int)ReadUInt32(rowOffset + 12);

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
        if (!string.IsNullOrEmpty(City) && City != "-")
            return $"{City}, {CountryCode}";
        if (!string.IsNullOrEmpty(Region) && Region != "-")
            return $"{Region}, {CountryCode}";
        return Country;
    }
}
