# Load test against https://hz.urza.cc/

Started 2026-10-18 23:21, ran 00:06:47. One row per stretch of unchanged settings that lasted 30 s or more with users running; the shorter stretches are the moments between two key presses. timeline.csv has every second.

| Started | Length | Users | Kinds | Pace | Msg sent | Msg/s | Send to appear p50/p95/p99 ms | Fan-out p95/p99 ms | HTTP p95 ms | RTT p95 ms | Errors | Reconnects | CPU avg/max % | Memory max MB | Pool queue max | Writer queue max |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 23:22:09 | 00:00:57 | 1000 | all | normal | 450 | 7.9 | 75 / 100 / 150 | 150 / 150 | 150 | 75 | 0 | 0 | 34 / 54 | 455 | 655 | 509 |
| 23:23:06 | 00:00:54 | 1250 | all | normal | 880 | 16.0 | 100 / 150 / 500 | 200 / 1000 | 150 | 150 | 0 | 0 | 56 / 64 | 494 | 1107 | 748 |
| 23:24:01 | 00:00:39 | 1500 | all | normal | 882 | 22.1 | 100 / 200 / 500 | 200 / 500 | 200 | 150 | 0 | 0 | 64 / 73 | 536 | 1474 | 2259 |
| 23:24:41 | 00:02:31 | 1750 | all | normal | 4153 | 27.4 | 150 / 300 / 750 | 500 / 1000 | 300 | 200 | 125 | 0 | 67 / 75 | 627 | 1826 | 2570 |
| 23:27:12 | 00:01:19 | 2000 | all | normal | 2512 | 31.8 | 200 / 500 / 1000 | 750 / 1000 | 750 | 500 | 82 | 0 | 69 / 77 | 709 | 2083 | 2598 |

Errors by reason: upload timeout: 207

Percentiles are the upper bound of a log-spaced bucket (1, 2, 3, 5, 8, 10, 15, 20, 30, 50, 75, 100, 150, 200, 300, 500, 750, 1000, ... ms), never optimistic.
