using System.Collections.Concurrent;
using TradingHub.Feeds;
using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>
/// Todos los datos de un instrumento: operaciones (historia + vivo), libro y estado de la conexión.
/// Los feeds escriben desde sus hilos en colas; la UI llama a <see cref="Pump"/> en su hilo para
/// aplicar los cambios y disparar los eventos. Varios gráficos pueden compartir la misma instancia.
/// </summary>
public sealed class InstrumentData : IMarketDataSink, IDisposable
{
    private readonly ConcurrentQueue<Trade> _incoming = new();
    private readonly ConcurrentQueue<BookChange> _depthChanges = new();
    private volatile DepthSnapshot? _latestDepth;
    private volatile bool _depthFull;
    private volatile bool _historyMerged;
    private List<Trade>? _historyToMerge;
    private readonly object _historyLock = new();
    private IDisposable? _subscription;
    private CancellationTokenSource? _cts;
    private TickRecorder? _recorder;
    private FeedStatus _status = FeedStatus.Disconnected;
    private string? _statusMessage;
    private volatile bool _statusDirty;

    public InstrumentData(Instrument instrument, IMarketDataFeed feed)
    {
        Instrument = instrument;
        Feed = feed;
        Trades = new TradeStore(instrument);
    }

    public Instrument Instrument { get; }
    public IMarketDataFeed Feed { get; }
    public TradeStore Trades { get; }
    public DepthSnapshot Depth { get; private set; } = DepthSnapshot.Empty;

    public FeedStatus Status => _status;
    public string? StatusMessage => _statusMessage;
    public bool IsLoadingHistory { get; private set; }
    public double HistoryProgress { get; private set; }
    public string? HistoryError { get; private set; }

    /// <summary>Desde cuándo hay historia pedida/cargada (UTC).</summary>
    public DateTime HistoryFrom { get; private set; } = DateTime.MaxValue;

    /// <summary>Si está trayendo historia más vieja en segundo plano.</summary>
    public bool IsExtending { get; private set; }

    private List<Trade>? _olderToMerge;
    private DateTime _pendingFrom;

    /// <summary>Última operación del mercado cuando está cerrado (la historia se arma hasta ahí).</summary>
    private DateTime? _anchor;

    public decimal LastPrice => Trades.Count > 0 ? Trades.GetPrice(Trades.Count - 1) : 0m;
    public decimal BestBid => Depth.HasBid ? Instrument.FromTicks(Depth.BestBidTicks) : 0m;
    public decimal BestAsk => Depth.HasAsk ? Instrument.FromTicks(Depth.BestAskTicks) : 0m;
    
    public decimal OpenInterest { get; private set; }
    public event Action<decimal>? OpenInterestChanged;

    /// <summary>Operaciones nuevas agregadas al almacén (índice inicial, cantidad). Hilo de UI.</summary>
    public event Action<int, int>? TradesAppended;

    /// <summary>La historia terminó de cargarse (los gráficos deben reconstruirse). Hilo de UI.</summary>
    public event Action? HistoryLoaded;

    /// <summary>Cambió el libro. Hilo de UI.</summary>
    public event Action<DepthUpdate>? DepthUpdated;

    public event Action? StatusChanged;

    /// <summary>Carpeta donde se graban los ticks (null = no grabar).</summary>
    public string? RecordingRoot { get; set; }

    /// <summary>Si graba lo recibido en vivo (la carpeta se sigue leyendo aunque no grabe).</summary>
    public bool RecordLive { get; set; } = true;

    // ----------------------------------------------------------------- arranque

    /// <summary>Se suscribe al vivo y carga historia desde <paramref name="historyFrom"/>.</summary>
    public void Start(DateTime historyFrom)
    {
        Stop();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _historyMerged = false;
        IsLoadingHistory = true;
        HistoryFrom = historyFrom;
        HistoryProgress = 0;
        HistoryError = null;
        SetStatus(FeedStatus.Connecting, "Conectando…");

        if (Feed.IsLive && RecordingRoot != null && RecordLive)
            _recorder = new TickRecorder(RecordingRoot, Instrument);

        // 1) vivo primero (se acumula mientras carga la historia)
        try
        {
            _subscription = Feed.Subscribe(Instrument, this);
        }
        catch (Exception ex)
        {
            SetStatus(FeedStatus.Error, ex.Message);
        }

        // 2) historia: archivos locales + los huecos que falten desde el feed
        _ = Task.Run(async () =>
        {
            List<Trade> all;
            try
            {
                var from = historyFrom;
                if (Feed is ISessionAwareFeed session)
                {
                    // mercado cerrado (noche, fin de semana): el mismo largo de historia, terminando en la última operación
                    try
                    {
                        var last = await session.LastTradeTimeAsync(Instrument, ct).ConfigureAwait(false);
                        if (last is { } lt && Feed.Now - lt > TimeSpan.FromMinutes(10))
                        {
                            _anchor = lt;
                            from = lt - (Feed.Now - historyFrom);
                            HistoryFrom = from;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // sin la hora de la última operación se carga como siempre
                    }
                }

                all = await LoadRangeAsync(from, Feed.Now, long.MaxValue, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                HistoryError = ex.Message;
                all = new List<Trade>();
            }

            lock (_historyLock) _historyToMerge = all;
        }, ct);
    }

    /// <summary>
    /// Hasta qué antigüedad se piden ticks al exchange cuando faltan. Lo anterior (y sin ticks en disco)
    /// se completa con velas del exchange: así se cargan meses o años en segundos, como en cualquier plataforma.
    /// </summary>
    public TimeSpan TickWindow { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Máximo de operaciones reales que se leen de disco por carga (lo que no entra se completa con velas).</summary>
    public static int MaxDiskTrades { get; set; } = 30_000_000;

    /// <summary>Desde cuándo hay ticks reales (antes, la historia viene de velas del exchange).</summary>
    public DateTime RealTicksFrom { get; private set; } = DateTime.MaxValue;

    /// <summary>
    /// Lee lo grabado/descargado entre <paramref name="from"/> y <paramref name="to"/> y completa los huecos:
    /// lo reciente con ticks del exchange (se graban para no volver a pedirlos) y lo viejo con velas.
    /// </summary>
    private async Task<List<Trade>> LoadRangeAsync(DateTime from, DateTime to, long maxIdExclusive, CancellationToken ct)
    {
        var real = new List<Trade>();
        if (RecordingRoot != null && Feed.IsLive)
        {
            // de lo más nuevo a lo más viejo, hasta el tope de memoria
            var chunks = new List<List<Trade>>();
            long count = 0;
            foreach (var day in EachDay(from, to).Reverse())
            {
                var path = TickFile.DayPath(RecordingRoot, Instrument.FeedId, Instrument.Symbol, day);
                if (!File.Exists(path)) continue;
                var list = TickFile.Read(path).Where(t => t.Time >= from && t.Time < to && t.Id < maxIdExclusive).ToList();
                if (count > 0 && count + list.Count > MaxDiskTrades) break;
                chunks.Add(list);
                count += list.Count;
            }

            chunks.Reverse();
            foreach (var c in chunks) real.AddRange(c);
            real = TickFile.SortAndDedupe(real);
        }

        if (!Feed.SupportsHistory) return real;
        var gaps = FindGaps(real, from, to, tailById: maxIdExclusive == long.MaxValue);
        if (gaps.Count == 0) return real;

        // lo reciente en ticks; lo viejo en velas (si el feed las tiene)
        var klineSource = Feed as IKlineHistory;
        // los ticks cubren las últimas horas de mercado (con el mercado cerrado, antes de la última operación)
        DateTime tickCut = klineSource != null ? (_anchor ?? Feed.Now) - TickWindow : DateTime.MinValue;
        var tickGaps = new List<(DateTime From, DateTime To, long? FromId)>();
        var klineGaps = new List<(DateTime From, DateTime To)>();
        foreach (var (gFrom, gTo, fromId) in gaps)
        {
            if (gTo <= tickCut) klineGaps.Add((gFrom, gTo));
            else if (gFrom >= tickCut) tickGaps.Add((gFrom, gTo, fromId));
            else
            {
                klineGaps.Add((gFrom, tickCut));
                tickGaps.Add((tickCut, gTo, null));
            }
        }

        int parts = tickGaps.Count + (klineGaps.Count > 0 ? 1 : 0);
        int part = 0;
        var fetched = new List<Trade>();
        foreach (var (gFrom, gTo, fromId) in tickGaps)
        {
            int index = part++;
            var progress = new Progress<double>(p => HistoryProgress = (index + Math.Clamp(p, 0, 1)) / parts);
            await Feed.LoadTradesAsync(Instrument, gFrom, gTo, fromId, batch =>
            {
                foreach (var t in batch)
                    if (t.Id < maxIdExclusive && t.Time >= from && t.Time <= to) fetched.Add(t);
                return !ct.IsCancellationRequested;
            }, progress, ct).ConfigureAwait(false);
        }

        if (fetched.Count > 0)
        {
            _recorder?.WriteHistory(fetched);
            real.AddRange(fetched);
            real = TickFile.SortAndDedupe(real);
        }

        if (real.Count > 0) RealTicksFrom = real[0].Time < RealTicksFrom ? real[0].Time : RealTicksFrom;
        if (klineGaps.Count == 0 || klineSource == null) return real;

        // velas solo donde no hay ticks reales
        var windows = KlineHistory.Subtract(klineGaps, KlineHistory.Coverage(real));
        if (windows.Count == 0) return real;
        var kFrom = windows[0].From;
        var kTo = windows[^1].To;
        int minutes = KlineHistory.MinutesFor(kTo - kFrom);
        decimal endPrice = real.Count > 0 ? real[0].Price : Trades.Count > 0 ? Trades.GetPrice(0) : 0;
        int kIndex = part;
        var kProgress = new Progress<double>(p => HistoryProgress = (kIndex + Math.Clamp(p, 0, 1)) / parts);
        var klines = await KlineHistory.LoadAsync(klineSource, Instrument, kFrom, kTo, minutes, endPrice, kProgress, ct).ConfigureAwait(false);
        var synthetic = new List<Trade>(klines.Count * 4);
        KlineHistory.Synthesize(klines, Instrument, minutes, windows, synthetic);
        return KlineHistory.MergeByTime(synthetic, real);
    }

    /// <summary>Rangos que faltan en <paramref name="local"/> (ordenado) para cubrir [from, to).</summary>
    public static List<(DateTime From, DateTime To, long? FromId)> FindGaps(IReadOnlyList<Trade> local, DateTime from, DateTime to, bool tailById)
    {
        var gaps = new List<(DateTime, DateTime, long?)>();
        if (local.Count == 0)
        {
            gaps.Add((from, to, null));
            return gaps;
        }

        if (local[0].Time - from > TimeSpan.FromMinutes(1)) gaps.Add((from, local[0].Time, null));
        for (int i = 1; i < local.Count; i++)
        {
            if (local[i].Time - local[i - 1].Time > TimeSpan.FromMinutes(3))
                gaps.Add((local[i - 1].Time, local[i].Time, null));
        }

        // demasiados cortes (instrumento poco líquido): conviene pedir todo de una
        if (gaps.Count > 20)
        {
            gaps.Clear();
            gaps.Add((from, to, null));
            return gaps;
        }

        if (to - local[^1].Time > TimeSpan.FromSeconds(1))
            gaps.Add((local[^1].Time, to, tailById ? local[^1].Id + 1 : null));
        return gaps;
    }

    /// <summary>
    /// Trae historia más vieja hasta <paramref name="from"/> (si hace falta): primero de los archivos
    /// grabados/descargados y lo que falte desde el feed. Al terminar se reconstruyen los gráficos.
    /// </summary>
    public bool ExtendHistory(DateTime from)
    {
        if (from >= HistoryFrom || IsLoadingHistory || IsExtending) return false;
        var ct = _cts?.Token ?? CancellationToken.None;
        DateTime until = Trades.Count > 0 ? Trades.FirstTime : Feed.Now;
        // si lo primero que hay es historia en velas (ids negativos), los ids no sirven para cortar
        long firstId = Trades.Count > 0 && Trades.GetId(0) > 0 ? Trades.GetId(0) : long.MaxValue;
        IsExtending = true;
        HistoryProgress = 0;
        _pendingFrom = from;
        _ = Task.Run(async () =>
        {
            var older = new List<Trade>();
            try
            {
                older = await LoadRangeAsync(from, until, firstId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HistoryError = ex.Message;
            }

            lock (_historyLock) _olderToMerge = older;
        }, ct);
        return true;
    }

    private long _lastDataAt;
    private int _tradedSinceAnchor;

    /// <summary>Cuándo llegó el último dato en vivo (operación o libro), en hora real UTC.</summary>
    public DateTime LastDataAt => new(Interlocked.Read(ref _lastDataAt), DateTimeKind.Utc);

    /// <summary>Al abrir, el mercado estaba cerrado (la última operación era vieja) y todavía no volvió a operar.</summary>
    public bool MarketClosed => _anchor != null && Volatile.Read(ref _tradedSinceAnchor) == 0;

    /// <summary>Última operación conocida cuando el mercado estaba cerrado al abrir.</summary>
    public DateTime? ClosedAt => _anchor;

    /// <summary>
    /// Fuerza la reconexión al instante (por ejemplo al despertar la Mac). Los feeds que lo soportan cortan
    /// la conexión y rellenan lo perdido; los demás se vuelven a suscribir.
    /// </summary>
    public void Reconnect()
    {
        if (_subscription is IReconnectable r)
        {
            r.Reconnect();
            return;
        }

        if (_subscription == null || !Feed.IsLive) return;
        _subscription.Dispose();
        try
        {
            _subscription = Feed.Subscribe(Instrument, this);
        }
        catch (Exception ex)
        {
            SetStatus(FeedStatus.Error, ex.Message);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _subscription?.Dispose();
        _subscription = null;
        _recorder?.Dispose();
        _recorder = null;
    }

    private static IEnumerable<DateOnly> EachDay(DateTime fromUtc, DateTime toUtc)
    {
        for (var d = DateOnly.FromDateTime(fromUtc); d <= DateOnly.FromDateTime(toUtc); d = d.AddDays(1))
            yield return d;
    }

    // ----------------------------------------------------------------- IMarketDataSink (hilos del feed)

    void IMarketDataSink.OnTrades(Instrument instrument, IReadOnlyList<Trade> trades)
    {
        foreach (var t in trades) _incoming.Enqueue(t);
        _recorder?.WriteLive(trades);
        _lastDataAt = DateTime.UtcNow.Ticks;
        if (trades.Count > 0 && _anchor != null) Volatile.Write(ref _tradedSinceAnchor, 1);
    }

    void IMarketDataSink.OnDepth(Instrument instrument, DepthUpdate update)
    {
        _lastDataAt = DateTime.UtcNow.Ticks;
        _latestDepth = update.Snapshot;
        if (update.IsFullRefresh) _depthFull = true;
        foreach (var c in update.Changes) _depthChanges.Enqueue(c);
        // evita que la cola crezca sin límite si la UI está trabada
        while (_depthChanges.Count > 50_000) _depthChanges.TryDequeue(out _);
    }

    void IMarketDataSink.OnStatus(Instrument instrument, FeedStatus status, string? message) => SetStatus(status, message);

    private void SetStatus(FeedStatus status, string? message)
    {
        _status = status;
        _statusMessage = message;
        _statusDirty = true;
    }

    void IMarketDataSink.OnOpenInterest(Instrument instrument, decimal openInterest, DateTime time)
    {
        OpenInterest = openInterest;
        OpenInterestChanged?.Invoke(openInterest);
    }
    
    // ----------------------------------------------------------------- hilo de UI

    /// <summary>Aplica lo recibido. Llamar periódicamente desde el hilo de UI.</summary>
    public void Pump()
    {
        if (_statusDirty)
        {
            _statusDirty = false;
            StatusChanged?.Invoke();
        }

        if (!_historyMerged)
        {
            List<Trade>? hist;
            lock (_historyLock)
            {
                hist = _historyToMerge;
                _historyToMerge = null;
            }

            if (hist != null)
            {
                Trades.Clear();
                foreach (var t in hist) Trades.Add(t);
                _historyMerged = true;
                IsLoadingHistory = false;
                HistoryProgress = 1;
                DrainIncoming(skipKnown: true, raise: false);
                HistoryLoaded?.Invoke();
                StatusChanged?.Invoke();
            }
        }
        else
        {
            List<Trade>? older;
            lock (_historyLock)
            {
                older = _olderToMerge;
                _olderToMerge = null;
            }

            if (older != null)
            {
                // reconstruye el almacén: historia vieja + lo que ya había
                if (older.Count > 0)
                {
                    var current = new List<Trade>(Trades.Count);
                    for (int i = 0; i < Trades.Count; i++) current.Add(Trades.Get(i));
                    Trades.Clear();
                    foreach (var t in older) Trades.Add(t);
                    foreach (var t in current) Trades.Add(t);
                }

                HistoryFrom = _pendingFrom;
                IsExtending = false;
                HistoryProgress = 1;
                if (older.Count > 0) HistoryLoaded?.Invoke();
                StatusChanged?.Invoke();
            }

            DrainIncoming(skipKnown: true, raise: true);
        }

        var depth = _latestDepth;
        if (depth != null && !ReferenceEquals(depth, Depth))
        {
            Depth = depth;
            var changes = new List<BookChange>();
            while (_depthChanges.TryDequeue(out var c)) changes.Add(c);
            bool full = _depthFull;
            _depthFull = false;
            DepthUpdated?.Invoke(new DepthUpdate { Snapshot = depth, Changes = changes, IsFullRefresh = full });
        }
    }

    private void DrainIncoming(bool skipKnown, bool raise)
    {
        if (_incoming.IsEmpty) return;
        int start = Trades.Count;
        long lastId = Trades.LastId;
        long lastTicks = Trades.Count > 0 ? Trades.GetTimeTicks(Trades.Count - 1) : long.MinValue;
        while (_incoming.TryDequeue(out var t))
        {
            // ya estaba: id conocido y no más nuevo que lo último guardado. Si un id raro quedó adelante
            // (archivo dañado), lo nuevo de verdad se reconoce por el tiempo y no se pierde el vivo.
            if (skipKnown && t.Id <= lastId && t.Time.Ticks <= lastTicks) continue;
            Trades.Add(t);
            lastId = Math.Max(lastId, t.Id);
            if (t.Time.Ticks > lastTicks) lastTicks = t.Time.Ticks;
        }

        int added = Trades.Count - start;
        if (added > 0 && raise) TradesAppended?.Invoke(start, added);
    }

    public void Dispose() => Stop();
}

/// <summary>Graba ticks en disco en un hilo aparte (no traba la UI).</summary>
public sealed class TickRecorder : IDisposable
{
    private readonly string _root;
    private readonly Instrument _instrument;
    private readonly BlockingCollection<Trade> _queue = new(new ConcurrentQueue<Trade>());
    private readonly Thread? _thread;
    private readonly FileStream? _lock;
    private TickFile.Writer? _writer;
    private DateOnly _day;

    public TickRecorder(string root, Instrument instrument)
    {
        _root = root;
        _instrument = instrument;
        // un solo proceso graba cada instrumento: si otro Trading Hub ya lo graba, este solo lee
        _lock = TickFile.TryLock(root, instrument.FeedId, instrument.Symbol);
        if (_lock == null) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "recorder-" + instrument.Symbol };
        _thread.Start();
    }

    /// <summary>False si otro proceso ya graba este instrumento (no se escribe nada).</summary>
    public bool IsActive => _lock != null;

    public void WriteLive(IReadOnlyList<Trade> trades)
    {
        if (_lock == null) return;
        foreach (var t in trades) _queue.TryAdd(t);
    }

    public void WriteHistory(IReadOnlyList<Trade> trades)
    {
        if (_lock == null) return;
        foreach (var t in trades) _queue.TryAdd(t);
    }

    private void Run()
    {
        try
        {
            foreach (var t in _queue.GetConsumingEnumerable())
            {
                var day = DateOnly.FromDateTime(t.Time);
                if (_writer == null || day != _day)
                {
                    _writer?.Dispose();
                    _day = day;
                    _writer = new TickFile.Writer(TickFile.DayPath(_root, _instrument.FeedId, _instrument.Symbol, day), _instrument);
                }

                _writer.Write(t);
                if (_queue.Count == 0) _writer.Flush();
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _writer?.Dispose();
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread?.Join(2000);
        _lock?.Dispose();
    }
}

/// <summary>Administra los <see cref="InstrumentData"/> abiertos (uno por feed+símbolo).</summary>
public sealed class MarketDataHub : IDisposable
{
    private readonly Dictionary<string, InstrumentData> _items = new();

    public string? RecordingRoot { get; set; }

    /// <summary>Si los instrumentos nuevos graban el vivo.</summary>
    public bool RecordLive { get; set; } = true;

    public IReadOnlyCollection<InstrumentData> Items => _items.Values;

    public InstrumentData GetOrStart(IMarketDataFeed feed, Instrument instrument, TimeSpan history)
    {
        if (_items.TryGetValue(instrument.Key, out var d)) return d;
        // ticks del exchange para lo reciente; lo más viejo se completa con velas (rápido aunque sean años)
        var window = history < TimeSpan.FromHours(2) ? TimeSpan.FromHours(2) : history > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : history;
        d = new InstrumentData(instrument, feed) { RecordingRoot = RecordingRoot, RecordLive = RecordLive, TickWindow = window };
        _items[instrument.Key] = d;
        d.Start(feed.Now - history);
        return d;
    }

    public void Release(InstrumentData data)
    {
        // los datos se mantienen mientras la app esté abierta (cambiar de gráfico es instantáneo)
    }

    public void PumpAll()
    {
        foreach (var d in _items.Values) d.Pump();
    }

    /// <summary>Reconecta todos los instrumentos en vivo (despertó la Mac, volvió internet...).</summary>
    public void ReconnectAll()
    {
        foreach (var d in _items.Values) d.Reconnect();
    }

    public void Dispose()
    {
        foreach (var d in _items.Values) d.Dispose();
        _items.Clear();
    }
}
