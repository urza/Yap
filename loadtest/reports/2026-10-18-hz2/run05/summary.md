# Load test against https://hz.urza.cc/

Started 2026-10-19 00:50, ran 00:07:48. One row per stretch of unchanged settings that lasted 30 s or more with users running; the shorter stretches are the moments between two key presses. timeline.csv has every second.

| Started | Length | Users | Kinds | Pace | Msg sent | Msg/s | Send to appear p50/p95/p99 ms | Fan-out p95/p99 ms | HTTP p95 ms | RTT p95 ms | Errors | Reconnects | CPU avg/max % | Memory max MB | Pool queue max | Writer queue max |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 00:50:50 | 00:02:48 | 1500 | all | normal | 3578 | 21.3 | 100 / 200 / 300 | 200 / 300 | 200 | 150 | 51 | 0 | 62 / 72 | 582 | 1508 | 2526 |
| 00:53:38 | 00:03:28 | 1750 | all | normal | 5925 | 28.4 | 150 / 300 / 300 | 300 / 300 | 300 | 150 | 205 | 0 | 67 / 76 | 760 | 2144 | 2773 |
| 00:57:07 | 00:01:19 | 2000 | all | normal | 2931 | 36.8 | 300 / 750 / 1000 | 750 / 1000 | 750 | 500 | 89 | 0 | 72 / 82 | 778 | 2447 | 1204 |

Errors by reason: upload timeout: 344, http timeout: 1

Percentiles are the upper bound of a log-spaced bucket (1, 2, 3, 5, 8, 10, 15, 20, 30, 50, 75, 100, 150, 200, 300, 500, 750, 1000, ... ms), never optimistic.
