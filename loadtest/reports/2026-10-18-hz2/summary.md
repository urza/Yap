# Load test against https://hz.urza.cc/

Started 2026-10-18 20:38, ran 00:05:53. One row per stretch of unchanged settings that lasted 30 s or more with users running; the shorter stretches are the moments between two key presses. timeline.csv has every second.

| Started | Length | Users | Kinds | Pace | Msg sent | Msg/s | Send to appear p50/p95/p99 ms | Fan-out p95/p99 ms | HTTP p95 ms | RTT p95 ms | Errors | Reconnects | CPU avg/max % | Memory max MB | Pool queue max | Writer queue max |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 20:39:56 | 00:03:56 | 800 | all | normal | 2933 | 12.4 | 100 / 150 / 200 | 150 / 200 | 300 | 100 | 7 | 0 | 47 / 54 | 563 | 806 | 628070 |

Errors by reason: upload timeout: 7

Percentiles are the upper bound of a log-spaced bucket (1, 2, 3, 5, 8, 10, 15, 20, 30, 50, 75, 100, 150, 200, 300, 500, 750, 1000, ... ms), never optimistic.
