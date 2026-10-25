// The place for the sun of the scene themes (ST-09). The inline script of index.html picks the Tea House scene from
// sunrise and sunset, and those need a latitude and a longitude. The place is the city of the device's time zone,
// from the table themes/teahouse/gen_zone_coords.py writes out of the tz database: no permission dialog, no network
// lookup of the person, no account field. The result goes to localStorage, where the inline script reads it before
// the first paint. One fetch per device and zone; a phone that lands in another zone fetches again. A zone without
// a city (UTC, Etc/GMT+1) stores the zone alone and the scenes follow the fixed 06:00 to 20:00 clock.

const KEY = 'yap.sun';   // "<zone>|<lat>|<lon>", or "<zone>" for a zone without a place

export async function syncScene() {
  const root = document.documentElement;
  if (root.dataset.theme !== 'teahouse') return;   // the only theme with [data-scene] blocks; a second one joins this test
  let zone, stored;
  try {
    zone = Intl.DateTimeFormat().resolvedOptions().timeZone;
    stored = localStorage.getItem(KEY);
  } catch { return; }   // private mode: nothing to store into, the fixed clock stays
  if (!zone || stored?.split('|')[0] === zone) return;
  const response = await fetch('/themes/zone-coords.json').catch(() => null);
  if (!response?.ok) return;
  const place = (await response.json().catch(() => ({})))[zone];
  try { localStorage.setItem(KEY, place ? `${zone}|${place[0]}|${place[1]}` : zone); } catch { return; }
  window.applyScene?.();
}
