from chatpath_svg import sequence, box, arrow, label, svg, esc


def S(frm, to, text, kind='call', note=None):
    return dict(frm=frm, to=to, text=text, kind=kind, note=note)


def parts():
    p = 'arch'
    o = []
    o.append('<line class="d-boundary" x1="500" y1="14" x2="500" y2="586"/>')
    o.append(label(250, 26, ['BROWSER'], 11, 'middle', 'd-muted', bg=False))
    o.append(label(750, 26, ['SERVER'], 11, 'middle', 'd-muted', bg=False))
    # browser
    o.append(box(20, 50, 180, 64, 'Page', ['app.js · composer.js', 'messages.js']))
    o.append(box(230, 50, 180, 64, 'Sender', ['sender.js', 'one per account']))
    o.append(box(20, 190, 180, 80, 'IndexedDB', ['storage.js', 'outbox · conversations', 'drafts · reads'], cls='d-store'))
    o.append(box(230, 330, 180, 64, 'acceptSnapshot', ['storage.commitUpdate', 'sync.mergeUpdate']))
    o.append(box(230, 500, 180, 64, 'SignalR client', ['signalr 10.0.0', 'two streams']))
    # server
    o.append(box(590, 50, 220, 64, 'ChatEndpoints', ['/api/chat/*', 'cookie · CSRF · protocol']))
    o.append(box(590, 140, 220, 64, 'ChatService', ['SendTextAsync', 'all messages in memory'], cls='d-lock'))
    o.append(box(840, 140, 140, 64, 'IChatStore', ['SQLite or memory'], cls='d-store'))
    o.append(box(590, 230, 220, 64, 'ChatChangeTracker', ['Touch · versions · sequence']))
    o.append(box(590, 320, 220, 64, 'ChatFanout', ['one patch per viewer']))
    o.append(box(590, 410, 220, 64, 'Subscription', ['one queue per connection', 'max 128 updates']))
    o.append(box(590, 500, 220, 64, 'ChatHub', ['/hubs/chat · WatchChanges']))
    # browser arrows
    o.append(arrow(p, [(70, 114), (70, 188)], 'call', 'enqueue()', 78, 155, 'start'))
    o.append(arrow(p, [(160, 190), (160, 116)], 'call', 'read', 168, 155, 'start'))
    o.append(arrow(p, [(200, 215), (270, 215), (270, 116)], 'call', 'outbox rows', 278, 170, 'start'))
    o.append(arrow(p, [(380, 114), (380, 328)], 'call', 'reply', 372, 290, 'end'))
    o.append(arrow(p, [(230, 362), (110, 362), (110, 272)], 'call', 'write', 170, 354))
    o.append(arrow(p, [(320, 500), (320, 396)], 'call', 'each item', 328, 452, 'start'))
    # wire
    o.append(arrow(p, [(410, 72), (588, 72)], 'http', 'POST …/messages', 500, 64))
    o.append(arrow(p, [(590, 96), (412, 96)], 'reply', '200 {update}', 500, 114))
    o.append(arrow(p, [(590, 532), (412, 532)], 'stream', 'stream item', 500, 524))
    # server arrows
    o.append(arrow(p, [(700, 114), (700, 138)], 'call', 'SendTextAsync()', 708, 130, 'start'))
    o.append(arrow(p, [(810, 172), (838, 172)], 'call'))
    o.append(label(910, 222, ['message + receipt', '+ unread counts'], 10, 'middle', 'd-muted', lh=13))
    o.append(arrow(p, [(700, 204), (700, 228)], 'call', 'Touch()', 708, 220, 'start'))
    o.append(arrow(p, [(700, 294), (700, 318)], 'call', 'event Changed', 708, 310, 'start'))
    o.append(arrow(p, [(700, 384), (700, 408)], 'call', 'Enqueue()', 708, 400, 'start'))
    o.append(arrow(p, [(700, 474), (700, 498)], 'call', 'Read()', 708, 490, 'start'))
    return svg(p, 1000, 590, o, 'The browser writes to an IndexedDB outbox; the sender posts each row to ChatEndpoints; ChatService stores it through IChatStore and calls Touch; ChatChangeTracker raises Changed; ChatFanout enqueues a patch per viewer; ChatHub reads the queue and streams it to the SignalR client; acceptSnapshot writes it to IndexedDB.')


def connect():
    lanes = [('P', 'Page', 'app.js'), ('D', 'IndexedDB', 'storage.js'), ('H', 'HTTP API', 'ChatEndpoints'), ('U', 'ChatHub', '/hubs/chat')]
    steps = [
        S('P', 'D', 'readState()'),
        S('P', 'P', 'render the cached chat', note='works with no network'),
        S('P', 'H', 'GET /api/chat/bootstrap', 'http', 'path, and the revision of the open conversation'),
        S('H', 'P', '{session, update}', 'reply', 'all conversations, the open one with messages'),
        S('P', 'D', 'commitUpdate(update)'),
        S('P', 'P', 'sender.start()', note='sends rows that are still in the outbox'),
        S('P', 'H', 'GET /api/chat/windows/{id}', 'http', 'one at a time, for each conversation without messages'),
        S('H', 'P', 'full window', 'reply'),
        S('P', 'U', 'start()', 'hub', 'negotiate, then WebSocket'),
        S('P', 'U', 'stream WatchActivity(ticket, …)', 'hub'),
        S('P', 'U', 'stream WatchChanges(known)', 'hub', 'known: cached revision per conversation'),
        S('U', 'P', 'first item: the current state', 'stream', 'header and one patch per conversation'),
    ]
    return sequence('con', lanes, steps, bands=[(6, 7, 'background: runs at the same time as steps 9 to 12')],
                    aria='Opening the chat: read IndexedDB, render, GET bootstrap, commit, start the sender, fetch missing windows, start the hub, open WatchActivity and WatchChanges.')


def send():
    lanes = [('P', 'Page', 'composer.js, app.js'), ('D', 'IndexedDB', 'outbox store'), ('N', 'Sender', 'sender.js'),
             ('H', 'HTTP API', 'ChatEndpoints'), ('C', 'ChatService', 'SendTextAsync'), ('T', 'IChatStore', 'SQLite or memory')]
    steps = [
        S('P', 'P', 'send(): clear the text box'),
        S('P', 'D', 'enqueue()', note='new row: operationId, queued'),
        S('P', 'P', 'renderPending(): show a grey row'),
        S('P', 'N', 'sender.start()'),
        S('N', 'D', "setDelivery('sending')"),
        S('N', 'H', 'POST …/messages', 'http', '{operationId, content}'),
        S('H', 'C', 'SendTextAsync()'),
        S('C', 'C', 'lock the operation key, look for a receipt', note='receipt found: return it, stop here'),
        S('C', 'T', 'save in one transaction', note='message, receipt, unread counts'),
        S('C', 'C', 'add the message to the memory list', note='under the conversation lock'),
        S('C', 'C', 'Touch(): tell the subscribers', note='the next section shows this'),
        S('H', 'N', '200 OK', 'reply', '{messageId, update}'),
        S('N', 'D', 'commitUpdate()', note='save the message, delete the row'),
        S('P', 'P', 'render(): the grey row goes, the message shows'),
    ]
    return sequence('snd', lanes, steps, bands=[(7, 10, 'server: the operation lock is held')],
                    aria='Sending: the page writes an outbox row, the sender posts it, ChatService checks the receipt, stores message and receipt in one transaction, adds the message to memory, calls Touch, and replies; the sender commits the reply, which deletes the row.')


def receive():
    lanes = [('C', 'ChatService', 'PublishMessageAsync'), ('G', 'ChatChangeTracker', 'Touch'), ('F', 'ChatFanout', 'Changed'),
             ('Q', 'Subscription', 'one per connection'), ('U', 'ChatHub', 'WatchChanges'), ('B', 'Browser', 'app.js, sync.js')]
    steps = [
        S('C', 'G', 'Touch(channelId, messageId)'),
        S('G', 'G', 'lock the conversation', note='version N becomes N + 1, take sequence S'),
        S('G', 'F', 'event Changed'),
        S('F', 'F', 'read the message from memory', note='build the DTO one time'),
        S('F', 'F', 'for each subscriber of the channel', note='check access, add the viewer flags'),
        S('F', 'Q', 'Enqueue(patch)', note='baseRevision N, revision N + 1'),
        S('Q', 'Q', 'TryWrite on Channel<bool>(1)', note='wakes the stream task'),
        S('U', 'Q', 'Read()', note='takes all pending patches'),
        S('U', 'B', 'yield patch', 'stream'),
        S('B', 'B', 'acceptSnapshot(): commitUpdate', note='mergeUpdate applies the patch'),
        S('B', 'B', 'render()'),
    ]
    return sequence('rcv', lanes, steps,
                    bands=[(0, 6, "sender's request thread: the conversation lock is held from step 2 to step 7"),
                           (7, 8, 'stream task of each subscriber'), (9, 10, 'browser of each subscriber')],
                    aria='Receiving: Touch locks the conversation, bumps the version and raises Changed; the fan-out builds a patch per subscriber and enqueues it; the subscriber stream task reads the queue and yields the patch; the browser commits and renders it.')


def merge_rule():
    # Same order as sync.js mergeUpdate: the revision check (lines 95-99) runs before
    # the messages are added (line 105). A refetch never blocks the add.
    p = 'mrg'
    o = []
    o.append(box(20, 30, 190, 90, 'A patch arrives', ['conversation C', 'baseRevision B', 'revision R, message M']))
    o.append(box(250, 30, 160, 90, 'L = B ?', ['L: local revision', 'of C'], cls='d-decide'))
    o.append(box(470, 30, 170, 90, 'Set L = R', ['the window', 'stays loaded']))
    o.append(box(250, 160, 160, 90, 'L = R ?', ['the patch is', 'a repeat'], cls='d-decide'))
    o.append(box(470, 160, 170, 90, 'Keep L', ['nothing to change']))
    o.append(box(250, 290, 160, 90, 'Mark C not loaded', ['sync.loaded = false']))
    o.append(box(700, 160, 130, 90, 'Add or replace M', ['if its sequence is', 'newer than the', 'last copy of M']))
    o.append(box(860, 160, 130, 90, 'windows.js', ['if C is not loaded:', 'GET /windows/{id}', 'replace window']))
    o.append(arrow(p, [(210, 75), (248, 75)]))
    o.append(arrow(p, [(410, 75), (468, 75)], 'call', 'yes', 439, 67))
    o.append(arrow(p, [(330, 120), (330, 158)], 'call', 'no', 338, 144, 'start'))
    o.append(arrow(p, [(410, 205), (468, 205)], 'call', 'yes', 439, 197))
    o.append(arrow(p, [(330, 250), (330, 288)], 'call', 'no', 338, 274, 'start'))
    o.append(arrow(p, [(640, 75), (765, 75), (765, 158)]))
    o.append(arrow(p, [(640, 205), (698, 205)]))
    o.append(arrow(p, [(410, 335), (765, 335), (765, 252)]))
    o.append(arrow(p, [(830, 205), (858, 205)]))
    return svg(p, 1000, 392, o, 'Merge rule in code order: if the local revision equals baseRevision, set it to revision; if it equals revision, keep it; otherwise mark the conversation not loaded. Then add the message if it is newer. A not-loaded conversation is refetched by windows.js.')


def outbox_states():
    p = 'obx'
    o = []
    o.append('<circle class="d-start" cx="26" cy="80" r="7"/>')
    o.append(box(110, 50, 170, 60, 'queued', ['grey row on screen']))
    o.append(box(400, 50, 170, 60, 'sending', ['POST in flight']))
    o.append(box(730, 50, 250, 60, 'accepted', ['row deleted, message shown'], cls='d-store'))
    o.append(box(400, 230, 170, 60, 'failed', ['red row with Retry'], cls='d-lock'))
    o.append(arrow(p, [(34, 80), (108, 80)], 'call', 'enqueue()', 71, 72))
    o.append(arrow(p, [(280, 70), (398, 70)], 'call', 'sender.flush()', 339, 62))
    o.append(arrow(p, [(570, 70), (728, 70)], 'call', 'server accepts', 649, 62))
    o.append(label(649, 88, ['POST reply or', 'stream copy'], 10, 'middle', 'd-muted', lh=13))
    o.append(arrow(p, [(430, 110), (430, 150), (195, 150), (195, 112)], 'call', 'retryable error', 312, 168,
                   note=['network, 5xx, 408, 429, csrf', 'wait 3 s × 2^n, max 60 s']))
    o.append(arrow(p, [(530, 110), (530, 228)], 'call', ['other 4xx, or', 'the 8th failure'], 538, 160, 'start'))
    o.append(arrow(p, [(400, 260), (150, 260), (150, 112)], 'call', 'user taps Retry', 275, 252))
    o.append('<rect class="d-note" x="640" y="190" width="340" height="104" rx="4"/>')
    o.append(label(656, 212, ['Server, for one operationId:'], 11, 'start', 'd-text', bg=False))
    o.append(label(656, 232, ['same content: returns the saved receipt', 'other content: 409 operation_conflict',
                              'receipt kept 1 day, newest 4096 per user'], 10, 'start', 'd-muted', bg=False, lh=16))
    return svg(p, 1000, 305, o, 'Outbox row states: queued, sending, accepted (deleted) or failed; retryable errors go back to queued with backoff; Retry moves failed to queued.')


def reconnect():
    lanes = [('B', 'Browser', 'app.js'), ('H', 'HTTP API', 'ChatEndpoints'), ('U', 'ChatHub', 'WatchChanges'), ('F', 'ChatFanout', 'Subscribe')]
    steps = [
        S('B', 'B', 'the stream fails or the connection closes', note='status: Disconnected · cached chat'),
        S('B', 'B', 'scheduleReconnect(): wait 2.5 to 3.5 s'),
        S('B', 'H', 'GET /api/chat/bootstrap', 'http', 'revision of the open conversation'),
        S('H', 'B', '{session, update}', 'reply'),
        S('B', 'B', 'commitUpdate(), sender.start()', note='queued rows go out again'),
        S('B', 'U', 'stream WatchChanges(known)', 'hub', 'known: cached revision per conversation'),
        S('U', 'F', 'Subscribe(user, known)'),
        S('F', 'F', 'Refresh(): compare each revision', note='under each conversation lock'),
        S('U', 'B', 'one patch per conversation', 'stream', 'invalidate = true where the revision differs'),
        S('B', 'B', 'mergeUpdate(): mark those not loaded'),
        S('B', 'H', 'GET /api/chat/windows/{id}', 'http', 'one at a time, the open conversation first'),
        S('H', 'B', 'full window', 'reply'),
    ]
    return sequence('rec', lanes, steps, aria='Reconnect: wait, bootstrap again, open WatchChanges with known revisions, the server compares revisions and invalidates changed conversations, the browser refetches their windows.')


def presence():
    lanes = [('B', 'Browser', 'live.js'), ('U', 'ChatHub', 'hub methods'), ('L', 'PresenceService', 'sessions, views'), ('C', 'ChatService', 'presence, typing')]
    steps = [
        S('B', 'U', 'send Report(visible, idle, channelId)', 'hub', 'every 10 s, every 60 s when hidden'),
        S('U', 'L', 'Report()', note='waits for the global gate'),
        S('L', 'C', 'ReportClientStateAsync()', note='visible, idle time, open channel'),
        S('B', 'U', 'send Typing(channelId, true)', 'hub', 'on the first key, then every 1.5 s'),
        S('U', 'L', 'Typing()'),
        S('L', 'C', 'StartTypingAsync()'),
        S('L', 'L', 'Tick(), every 100 ms', note='called by PresenceTicker'),
        S('L', 'C', 'GetAllUsersWithStatus(), GetTypingUsers()'),
        S('L', 'L', 'build one view per connection', note='skip it if nothing changed'),
        S('L', 'U', 'TryWrite(view)', note='Channel<PresenceView>(1) keeps the newest'),
        S('U', 'U', 'keep only the changed fields'),
        S('U', 'B', 'WatchActivity item', 'stream'),
    ]
    return sequence('pre', lanes, steps, bands=[(0, 5, 'hub calls from the browser'), (6, 11, 'the presence tick')],
                    aria='Presence: the browser sends Report and Typing; the live service updates ChatService; every 100 ms Tick reads statuses and typing, writes a view to a one-item channel per connection; WatchActivity streams the changed fields.')


def locks():
    p = 'lck'
    o = []
    cols = [['find the', 'receipt'], ['copy unread', 'counts'], ['store', 'transaction'], ['add to the', 'memory list'], ['Touch and', 'fan-out'], ['build the', 'POST reply']]
    x0, cw = 270, 120
    for i, (a, b) in enumerate(cols):
        cx = x0 + i * cw + cw / 2
        o.append(label(cx, 28, [f'{i + 1}. {a}', b], 11, 'middle', 'd-text', bg=False))
        o.append(f'<line class="d-life" x1="{x0 + i * cw}" y1="54" x2="{x0 + i * cw}" y2="236"/>')
    o.append(f'<line class="d-life" x1="{x0 + 6 * cw}" y1="54" x2="{x0 + 6 * cw}" y2="236"/>')
    rows = [('operation lock', 'SemaphoreSlim, per operationId', [(0, 5)]),
            ('readStateGate', 'SemaphoreSlim, one for all', [(1, 4)]),
            ('conversation lock', 'Monitor, per channel', [(3, 4), (4, 5), (5, 6)]),
            ('Subscription.gate', 'Monitor, per connection', [(4, 5)])]
    for r, (name, sub, spans) in enumerate(rows):
        y = 70 + r * 42
        o.append(label(20, y + 12, [name], 11, 'start', 'd-text', bg=False))
        o.append(label(20, y + 26, [sub], 10, 'start', 'd-muted', bg=False))
        for a, b in spans:
            thin = name == 'Subscription.gate'
            hy, hh = (y + 10, 12) if thin else (y + 4, 24)
            o.append(f'<rect class="d-bar" x="{x0 + a * cw + 4}" y="{hy}" width="{(b - a) * cw - 8}" height="{hh}" rx="3"/>')
    o.append(label(x0 + 4.5 * cw, 246, ['short, once per subscriber'], 10, 'middle', 'd-muted', bg=False))
    return svg(p, 1000, 256, o, 'Locks held on the request thread during one send: the operation lock for steps 1 to 5, readStateGate for steps 2 to 4, the conversation lock three separate times in steps 4, 5 and 6, and each subscription gate briefly in step 5.')


def queue():
    p = 'que'
    o = []
    o.append(box(20, 50, 150, 64, 'ChatFanout', ['request thread']))
    o.append('<rect class="d-lockarea" x="250" y="20" width="300" height="150" rx="6"/>')
    o.append(label(400, 40, ['lock (gate)'], 11, 'middle', 'd-text', bg=False))
    o.append(box(266, 52, 268, 44, 'Queue<ChatUpdate> pending', None, cls='d-store', size=11))
    o.append(box(266, 108, 120, 44, 'overflow flag', None, size=11))
    o.append(box(396, 108, 138, 44, 'Channel<bool>(1)', None, size=11))
    o.append(arrow(p, [(170, 82), (248, 82)], 'call', 'Enqueue()', 209, 74))
    o.append(box(610, 40, 170, 110, 'Read()', ['wait for the signal', 'take all pending', 'clear the flag', 'release the gate']))
    o.append(arrow(p, [(550, 130), (608, 130)], 'call', 'signal', 579, 122))
    o.append(box(830, 20, 150, 64, 'merge', ['flag not set:', 'one patch per conv.']))
    o.append(box(830, 106, 150, 64, 'Reset', ['flag set: all', 'conversations again'], cls='d-lock'))
    o.append(arrow(p, [(780, 70), (828, 52)]))
    o.append(arrow(p, [(780, 120), (828, 138)]))
    o.append(label(500, 206, ['At 128 pending updates, Enqueue clears the queue and sets the flag.',
                              'Lock order: conversation lock, then gate. Read() never takes them the other way.'], 10, 'middle', 'd-muted', bg=False, lh=15))
    return svg(p, 1000, 232, o, 'Subscription queue: Enqueue adds to a queue under a lock and writes to a one-item channel; Read waits on the channel, takes everything, and returns merged patches or, after an overflow, a Reset.')


ALL = dict(parts=parts, connect=connect, send=send, receive=receive, merge=merge_rule, outbox=outbox_states,
           reconnect=reconnect, presence=presence, locks=locks, queue=queue)
