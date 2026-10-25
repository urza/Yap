#!/usr/bin/env python3
"""Write the time zone to place table the Tea House sun schedule reads.

Reads  the tz database of this machine: /usr/share/zoneinfo/zone.tab, zone1970.tab and tzdata.zi
Writes ../../Yap/wwwroot/themes/zone-coords.json  as  {"Europe/Copenhagen": [55.7, 12.6], ...}

Why a table: the scenes follow sunrise and sunset, and those need a latitude and a longitude.
The browser gives the device's time zone name (Intl) without a prompt, and the tz database
names one city per zone with its coordinates. That city stands for the place. Nairobi for
Africa/Nairobi, Copenhagen for Europe/Copenhagen. A zone that spans many latitudes
(America/Chicago runs from North Dakota to Texas) is off by up to an hour of daylight, which
the two-hour scene steps absorb. No permission dialog, no network lookup, no account field.

Both zone tables are read: zone.tab has one zone per country (Europe/Copenhagen with its own
coordinates), zone1970.tab merges countries into one zone (Europe/Berlin for DK, NO, SE) and
has zones the older table lacks. Browsers return either name. The links in tzdata.zi
(Asia/Calcutta for Asia/Kolkata, Europe/Kiev for Europe/Kyiv) get the coordinates of their
target, so an older browser or OS still finds its zone.

The coordinates are rounded to one decimal (11 km). Sunrise moves about one minute per 15 km of
longitude and less than that per 11 km of latitude, so the rounding never shows.

Run on a Linux machine with tzdata installed, after a tzdata update that adds zones:
    python3 gen_zone_coords.py
"""
import json
import os
import re

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.abspath(os.path.join(HERE, "..", "..", "Yap", "wwwroot", "themes", "zone-coords.json"))
ZONEINFO = "/usr/share/zoneinfo"

# ISO 6709 as the tab files write it: +DDMM+DDDMM or +DDMMSS+DDDMMSS.
COORD = re.compile(r"^([+-])(\d{2})(\d{2})(\d{2})?([+-])(\d{3})(\d{2})(\d{2})?$")


def parse(coord):
    m = COORD.match(coord)
    if not m:
        raise ValueError("unexpected coordinate %r" % coord)
    lat = int(m[2]) + int(m[3]) / 60 + int(m[4] or 0) / 3600
    lon = int(m[6]) + int(m[7]) / 60 + int(m[8] or 0) / 3600
    if m[1] == "-":
        lat = -lat
    if m[5] == "-":
        lon = -lon
    return [round(lat, 1), round(lon, 1)]


def read_tab(name, places):
    with open(os.path.join(ZONEINFO, name), encoding="utf-8") as f:
        for line in f:
            if line.startswith("#") or not line.strip():
                continue
            _countries, coord, zone = line.rstrip("\n").split("\t")[:3]
            places.setdefault(zone, parse(coord))


def read_links(places):
    # "L <target> <alias>" lines. A target may itself be an alias; resolve until a real zone is found.
    links = {}
    with open(os.path.join(ZONEINFO, "tzdata.zi"), encoding="utf-8") as f:
        for line in f:
            if line.startswith("L "):
                _l, target, alias = line.split()
                links[alias] = target
    for alias, target in links.items():
        seen = set()
        while target in links and target not in places and target not in seen:
            seen.add(target)
            target = links[target]
        if target in places:
            places.setdefault(alias, places[target])


def main():
    places = {}
    read_tab("zone.tab", places)
    read_tab("zone1970.tab", places)
    read_links(places)
    # One zone per line: a tzdata update shows as a small diff.
    lines = ",\n".join('"%s":[%s,%s]' % (z, lat, lon) for z, (lat, lon) in sorted(places.items()))
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        f.write("{\n" + lines + "\n}\n")
    with open(OUT, encoding="utf-8") as f:
        json.load(f)   # the output must parse
    print("wrote %d zones to %s" % (len(places), OUT))


if __name__ == "__main__":
    main()
