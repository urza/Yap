# Load test against https://hz.urza.cc/

Started 2026-10-19 01:15, ran 00:11:21. One row per stretch of unchanged settings that lasted 30 s or more with users running; the shorter stretches are the moments between two key presses. timeline.csv has every second.

| Started | Length | Users | Kinds | Pace | Msg sent | Msg/s | Send to appear p50/p95/p99 ms | Fan-out p95/p99 ms | HTTP p95 ms | RTT p95 ms | Errors | Reconnects | CPU avg/max % | Memory max MB | Pool queue max | Writer queue max |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 01:16:00 | 00:00:32 | 1000 | all | normal | 110 | 3.4 | 75 / 100 / 150 | 100 / 150 | 100 | 75 | 0 | 0 | 37 / 66 | 593 | 240 | 40 |
| 01:17:02 | 00:00:44 | 1500 | all | normal | 996 | 22.3 | 100 / 200 / 300 | 200 / 300 | 200 | 150 | 0 | 0 | 67 / 73 | 700 | 1408 | 869 |
| 01:17:46 | 00:00:45 | 1750 | all | normal | 1260 | 27.5 | 150 / 300 / 500 | 300 / 500 | 500 | 300 | 10 | 0 | 72 / 79 | 759 | 1716 | 2773 |
| 01:18:50 | 00:00:46 | 2000 | all | normal | 1624 | 35.1 | 200 / 750 / 1000 | 750 / 1000 | 750 | 750 | 52 | 0 | 72 / 82 | 806 | 2203 | 3336 |
| 01:19:52 | 00:00:41 | 2448 of 2500 | all | normal | 1349 | 32.3 | 1500 / 3000 / 5000 | 3000 / 5000 | 3000 | 3000 | 49 | 0 | 85 / 91 | 948 | 3577 | 3562 |
| 01:20:34 | 00:00:58 | 2500 of 2750 | all | normal | 2529 | 43.0 | 750 / 5000 / 10000 | 5000 / 10000 | 5000 | 5000 | 69 | 0 | 82 / 92 | 1090 | 4823 | 7618 |
| 01:21:33 | 00:01:00 | 2500 of 3500 | all | normal | 2591 | 42.8 | 750 / 1500 / 2000 | 1500 / 2000 | 2000 | 750 | 90 | 0 | 79 / 84 | 1112 | 3960 | 15029 |
| 01:22:33 | 00:04:42 | 4000 | all | normal | 3551 | 12.6 | 3000 / 47820 / 47820 | 74122 / 74122 | 30001 | 62655 | 31818 | 27935 | 68 / 95 | 1608 | 6795 | 18508 |

Errors by reason: upload timeout: 476, http 404: 108, hub OperationCanceledException: 4198, hub closed: 3274, behaviour ObjectDisposedException: 66, hub HubException: 44, hub AggregateException: 4869, hub timeout: 1535, http 502: 1360, hub 502: 10079, upload HttpRequestException: 46, http timeout: 491, behaviour timeout: 45, no echo in 60 s: 297, behaviour HubException: 117, behaviour InvalidOperationException: 21, hub HttpRequestException: 4957, http HttpRequestException: 139

Percentiles are the upper bound of a log-spaced bucket (1, 2, 3, 5, 8, 10, 15, 20, 30, 50, 75, 100, 150, 200, 300, 500, 750, 1000, ... ms), never optimistic.
