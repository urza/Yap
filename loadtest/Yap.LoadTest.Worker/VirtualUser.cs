using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Yap.LoadTest.Shared;

namespace Yap.LoadTest.Worker;

/// <summary>
/// One person at one browser tab. It signs in with a saved token, connects the hub the way hub.js does (one
/// connection, heartbeat every 10 s when visible, every 60 s when hidden, its own reconnect loop), opens a room
/// and then behaves: thinks, types, sends, reacts, reads, hops rooms, goes idle. Every measurement goes to Stats.
/// </summary>
public sealed class VirtualUser(Context ctx, string username, string token, int index)
{
    private readonly Random _rng = new(HashCode.Combine(username, ctx.Worker));
    private readonly string _device = $"lt-{Guid.NewGuid():N}";
    private HubConnection? _hub;
    private volatile bool _connected;
    private volatile bool _visible;
    private Guid _channel;           // the open conversation (a room; a DM is sent by POST and never opened)
    private Guid _homeRoom;
    private List<RoomDto> _rooms = [];
    private List<string> _peers = [];  // other test users, for DMs
    private string _uploadBase = "";
    private DateTime _lastInputAt = DateTime.UtcNow;
    private DateTime _lastMarkReadAt = DateTime.MinValue;
    private int? _lastRttMs;
    private readonly List<int> _sendToAppearSamples = [];
    private readonly Dictionary<Guid, long> _inFlight = [];        // clientId -> Stopwatch timestamp of the POST
    private readonly Queue<Guid> _recent = new();                  // message ids seen in the open room, for reactions and replies
    private readonly Lock _gate = new();
    private const double FreshUploadShare = 0.1;   // image sends that upload a new file instead of reusing the pool
    private const double RoomHopChance = 0.1;      // after a send, when there are test rooms: open another one for a while

    public async Task RunAsync(CancellationToken ct)
    {
        ctx.Stats.UserStarted();
        try
        {
            _visible = _rng.NextDouble() < ctx.VisibleShare;
            var heartbeat = HeartbeatLoopAsync(ct);
            var behaviour = BehaviourLoopAsync(ct);
            await ConnectionLoopAsync(ct);
            await Task.WhenAll(heartbeat, behaviour);
        }
        finally { ctx.Stats.UserStopped(); }
    }

    // ---- connection -------------------------------------------------------------------------------------

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hub = new HubConnectionBuilder()
                .WithUrl(new Uri(ctx.Api.BaseUri, $"/hub?device={_device}"), o =>
                {
                    o.Headers["Cookie"] = $"yap_auth={token}";
                    o.Transports = HttpTransportType.WebSockets;   // what the browser gets; a fallback transport is a different test
                })
                .AddJsonProtocol(o => o.PayloadSerializerOptions = Json.Options)
                .Build();
            hub.ServerTimeout = TimeSpan.FromSeconds(60);
            hub.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
            Register(hub);
            try
            {
                await hub.StartAsync(ct);
                attempt = 0;
                _hub = hub;
                _connected = true;
                ctx.Stats.Connected(true);
                await OnConnectedAsync(hub, ct);
                await closed.Task.WaitAsync(ct);
                ctx.Stats.Error("hub closed");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Unauthorized) { ctx.Stats.Error("hub 401"); break; }   // signed out: hub.js stops too
            catch (Exception e) { ctx.Stats.Error("hub " + Reason(e)); }
            finally
            {
                if (_connected) { _connected = false; ctx.Stats.Connected(false); }
                _hub = null;
                await hub.DisposeAsync();
            }
            if (ct.IsCancellationRequested) break;
            ctx.Stats.Reconnect();
            attempt++;
            await Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(attempt - 1, 5)))), ct);   // hub.js: 1, 2, 4, 8, 16, 30 s
        }
    }

    private void Register(HubConnection hub)
    {
        hub.On<MessageDto>("message", OnMessage);
        foreach (var name in new[] { "typing", "presences", "unread", "reactions", "previews", "messageEdited", "messageDeleted", "rooms", "me", "profile", "dm", "gifFavorites" })
            hub.On(name, Array.Empty<Type>(), _ => { ctx.Stats.Event(); return Task.CompletedTask; });
    }

    private async Task OnConnectedAsync(HubConnection hub, CancellationToken ct)
    {
        // The real client bootstraps on every connect, in parallel with opening the room.
        var bootstrap = ctx.Api.GetAsync<BootstrapDto>($"/api/bootstrap?connection={hub.ConnectionId}", token, ct);
        var b = await bootstrap ?? throw new InvalidOperationException("empty bootstrap");
        _rooms = b.Rooms ?? [];
        _uploadBase = b.Config?.UploadUrl ?? "";
        _peers = (b.Users ?? []).Where(u => !u.IsBot && !u.IsAdmin && u.Username != username && u.Username.StartsWith(ctx.Prefix)).Select(u => u.Username).ToList();
        if (_homeRoom == Guid.Empty) _homeRoom = HomeRoom();
        if (_channel == Guid.Empty) _channel = _homeRoom;
        await OpenAsync(hub, _channel, ct);
        if (ctx.ClaimCatalogFetch()) _ = FetchCatalogAsync(ct);
    }

    // Test rooms are lt-room-NN; user i lives in room i mod N. One room means the lobby.
    private Guid HomeRoom()
    {
        var testRooms = _rooms.Where(r => r.Name.StartsWith("lt-room-")).OrderBy(r => r.Name).ToList();
        if (ctx.Rooms <= 1 || testRooms.Count == 0) return _rooms.FirstOrDefault(r => r.IsLobby)?.Id ?? _rooms.First().Id;
        return testRooms[index % testRooms.Count].Id;
    }

    private async Task OpenAsync(HubConnection hub, Guid channelId, CancellationToken ct)
    {
        var conversation = await hub.InvokeAsync<Conversation>("OpenRoom", channelId, ct);
        lock (_gate)
        {
            _channel = channelId;
            _recent.Clear();
            foreach (var m in conversation.Messages.TakeLast(50)) _recent.Enqueue(m.Id);
        }
    }

    private async Task FetchCatalogAsync(CancellationToken ct)
    {
        try
        {
            var catalog = await ctx.Api.GetAsync<EmojiCatalog>("/api/emoji", token, ct);
            ctx.Shortcodes = (catalog?.Packs ?? []).SelectMany(p => p.Emojis).Select(e => e.Shortcode).ToList();
        }
        catch { /* custom emoji are optional; the mix falls back to unicode */ }
    }

    // ---- incoming ---------------------------------------------------------------------------------------

    private void OnMessage(MessageDto m)
    {
        ctx.Stats.Event();
        ctx.Stats.Received();
        var now = Stopwatch.GetTimestamp();
        var mine = false;
        lock (_gate)
        {
            if (m.ClientId is { } own && _inFlight.Remove(own, out var started))
            {
                mine = true;
                var ms = (int)Stopwatch.GetElapsedTime(started, now).TotalMilliseconds;
                ctx.Stats.SendToAppear.Add(ms);
                _sendToAppearSamples.Add(ms);   // goes to the server with the next heartbeat, as hub.js does (AD-26)
            }
            if (m.ChannelId == _channel)
            {
                _recent.Enqueue(m.Id);
                while (_recent.Count > 50) _recent.Dequeue();
            }
        }
        if (mine || m.Username == username) return;
        if (m.ClientId is { } other && ClientIds.TryDecode(other, out var sentMs, out _))
            ctx.Stats.FanOut.Add(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sentMs);
        // A visible reader at the bottom marks the room read; at most every 2 s, as the timeline does.
        if (_visible && m.ChannelId == _channel && DateTime.UtcNow - _lastMarkReadAt > TimeSpan.FromSeconds(2) && (DateTime.UtcNow - _lastInputAt).TotalSeconds < 300)
        {
            _lastMarkReadAt = DateTime.UtcNow;
            _ = _hub?.SendAsync("MarkRead", m.ChannelId);
        }
    }

    // ---- heartbeat ---------------------------------------------------------------------------------------

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var ticks = 0;
        while (!ct.IsCancellationRequested)
        {
            await Delay(TimeSpan.FromSeconds(10), ct);
            if (ct.IsCancellationRequested) break;
            ticks++;
            if (!_visible && ticks % 6 != 0) continue;
            await BeatAsync(ct);
        }
    }

    private async Task BeatAsync(CancellationToken ct)
    {
        if (_hub is not { } hub || !_connected) return;
        int[]? samples;
        lock (_gate) { samples = _sendToAppearSamples.Count > 0 ? _sendToAppearSamples.ToArray() : null; _sendToAppearSamples.Clear(); }
        var idle = (int)(DateTime.UtcNow - _lastInputAt).TotalSeconds;
        var started = Stopwatch.GetTimestamp();
        try
        {
            await hub.InvokeAsync<long>("Heartbeat", _visible, idle, _lastRttMs, samples, ct);
            _lastRttMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            ctx.Stats.Rtt.Add(_lastRttMs.Value);
        }
        catch (OperationCanceledException) { }
        catch { /* the connection is going down; the connection loop sees it */ }
    }

    // ---- behaviour ---------------------------------------------------------------------------------------

    private async Task BehaviourLoopAsync(CancellationToken ct)
    {
        await Delay(TimeSpan.FromSeconds(_rng.Next(2, 15)), ct);   // not everyone acts at the same moment after the ramp
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_visible)
                {
                    // A hidden tab: minutes of nothing, then maybe the person comes back.
                    await Delay(TimeSpan.FromSeconds(Exponential(240)), ct);
                    if (_rng.NextDouble() < 0.4) { _visible = true; Input(); await BeatAsync(ct); }
                    continue;
                }
                // The pace and the kinds are read on every turn, so a change on the dashboard applies within one think time.
                var pace = ctx.Controls.Pace;
                await Delay(TimeSpan.FromSeconds(Exponential(pace.ThinkSeconds())), ct);
                if (ct.IsCancellationRequested) break;
                if (_rng.NextDouble() < 0.04) { await Delay(TimeSpan.FromSeconds(_rng.Next(120, 420)), ct); continue; }   // stepped away: auto-away after 5 min
                if (_rng.NextDouble() < 0.03) { _visible = false; await BeatAsync(ct); continue; }                          // switched tabs
                if (_hub is not { } hub || !_connected) continue;

                var on = ctx.Controls.Kinds;
                if (Traffic.Pick(_rng, on) is not { } kind) continue;   // nothing is on: the user reads and heartbeats
                if (kind == MessageKind.Reaction) { await ReactAsync(hub, ct); continue; }

                await TypeAsync(hub, kind, ct);
                await SendAsync(kind, ct);
                if (on.HasFlag(Kinds.Text) && _rng.NextDouble() < pace.BurstChance())
                    for (var i = _rng.Next(2, 6); i > 0 && !ct.IsCancellationRequested; i--)
                    {
                        await Delay(TimeSpan.FromMilliseconds(_rng.Next(400, 2500)), ct);
                        await SendAsync(_rng.Next(4) == 0 ? MessageKind.Emoji : MessageKind.Short, ct);
                    }
                if (ctx.Rooms > 1 && _rng.NextDouble() < RoomHopChance) await HopAsync(hub, ct);
            }
            catch (Exception) when (ct.IsCancellationRequested) { }   // the user is being stopped: the hub is disposed under the running action
            catch (Exception e) { ctx.Stats.Error("behaviour " + Reason(e)); await Delay(TimeSpan.FromSeconds(5), ct); }
        }
    }

    private async Task TypeAsync(HubConnection hub, MessageKind kind, CancellationToken ct)
    {
        Input();
        var seconds = kind switch { MessageKind.Long => _rng.Next(6, 20), MessageKind.Emoji or MessageKind.Image => 1, _ => _rng.Next(1, 5) };
        for (var t = 0; t < seconds; t += 2)   // the composer repeats typing(true) every 2 s while keys fall
        {
            await hub.SendAsync("Typing", _channel, true, ct);
            await Delay(TimeSpan.FromSeconds(Math.Min(2, seconds - t)), ct);
        }
    }

    private async Task SendAsync(MessageKind kind, CancellationToken ct)
    {
        Input();
        string? text = null; List<string>? images = null; Guid? replyTo = null;
        switch (kind)
        {
            case MessageKind.Short: text = Content.Short(_rng); break;
            case MessageKind.Long: text = Content.Long(_rng); break;
            case MessageKind.Emoji: text = Content.EmojiOnly(_rng); break;
            case MessageKind.CustomEmoji: text = Content.WithCustomEmoji(_rng, ctx.Shortcodes); break;
            case MessageKind.Link: text = Content.Link(_rng, ctx.Links); break;
            case MessageKind.Reply: text = Content.Short(_rng); lock (_gate) replyTo = _recent.Count > 0 ? _recent.ElementAt(_rng.Next(_recent.Count)) : null; break;
            case MessageKind.Image:
                if (await ImageUrlAsync(ct) is { } url) { images = [url]; if (_rng.Next(2) == 0) text = Content.Short(_rng); }
                else text = Content.Short(_rng);
                break;
            case MessageKind.Dm: text = Content.Short(_rng); break;
        }
        var path = kind == MessageKind.Dm && _peers.Count > 0 ? $"/api/dm/{_peers[_rng.Next(_peers.Count)]}/messages" : $"/api/channels/{_channel}/messages";
        var clientId = ClientIds.New(ctx.Worker);
        var started = Stopwatch.GetTimestamp();
        lock (_gate) _inFlight[clientId] = started;
        try
        {
            using var response = await ctx.Api.SendJsonAsync(HttpMethod.Post, path, new SendMessageRequest(clientId, text, replyTo, images), token, ct);
            ctx.Stats.Http.Add((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (response.IsSuccessStatusCode) ctx.Stats.Sent();
            else { ctx.Stats.Error($"http {(int)response.StatusCode}"); lock (_gate) _inFlight.Remove(clientId); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { ctx.Stats.Error("http " + Reason(e)); lock (_gate) _inFlight.Remove(clientId); }
        // An own message that never comes back on the hub would sit in the map for ever.
        lock (_gate) foreach (var stale in _inFlight.Where(kv => Stopwatch.GetElapsedTime(kv.Value).TotalSeconds > 60).Select(kv => kv.Key).ToList()) { _inFlight.Remove(stale); ctx.Stats.Error("no echo in 60 s"); }
    }

    private async Task<string?> ImageUrlAsync(CancellationToken ct)
    {
        var pool = ctx.ImagePool;
        var fresh = pool.Count < Context.ImagePoolSize || _rng.NextDouble() < FreshUploadShare;
        if (!fresh) return pool.ElementAt(_rng.Next(pool.Count));
        try
        {
            var jpeg = Images.RandomJpeg(_rng);
            var url = await ctx.Api.UploadImageAsync(token, jpeg, $"photo-{_rng.Next(100000)}.jpg", _uploadBase, ct);
            ctx.Stats.Upload();
            if (pool.Count < Context.ImagePoolSize) pool.Add(url);
            return url;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { ctx.Stats.Error("upload " + Reason(e)); return pool.IsEmpty ? null : pool.ElementAt(_rng.Next(pool.Count)); }
    }

    private async Task ReactAsync(HubConnection hub, CancellationToken ct)
    {
        Guid target;
        lock (_gate) { if (_recent.Count == 0) return; target = _recent.ElementAt(_rng.Next(_recent.Count)); }
        Input();
        var emoji = Content.OneEmoji(_rng);
        await hub.SendAsync("EmojiUsed", emoji, ct);
        await hub.InvokeAsync<object?>("React", target, emoji, ct);
        ctx.Stats.Reaction();
    }

    private async Task HopAsync(HubConnection hub, CancellationToken ct)
    {
        var others = _rooms.Where(r => r.Id != _channel && (r.IsLobby || r.Name.StartsWith("lt-room-"))).ToList();
        if (others.Count == 0) return;
        await OpenAsync(hub, others[_rng.Next(others.Count)].Id, ct);
        await Delay(TimeSpan.FromSeconds(_rng.Next(20, 180)), ct);
        if (_connected && _hub is { } h) await OpenAsync(h, _homeRoom, ct);
    }

    private void Input() => _lastInputAt = DateTime.UtcNow;
    private double Exponential(double mean) => -mean * Math.Log(1 - _rng.NextDouble());
    private static async Task Delay(TimeSpan d, CancellationToken ct) { try { await Task.Delay(d, ct); } catch (OperationCanceledException) { } }
    private static string Reason(Exception e) => e switch
    {
        HttpRequestException { StatusCode: { } s } => ((int)s).ToString(),
        TaskCanceledException => "timeout",
        _ => e.GetType().Name,
    };
}
