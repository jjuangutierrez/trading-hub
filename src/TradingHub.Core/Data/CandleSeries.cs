using TradingHub.Market;

namespace TradingHub.Data;

/// <summary>Resultado de agregar operaciones a una serie.</summary>
public readonly record struct AppendResult(int FirstChangedBar, int NewCandles)
{
    public static readonly AppendResult None = new(-1, 0);
    public bool Changed => FirstChangedBar >= 0;
}

/// <summary>
/// Serie de velas con footprint construida a partir de un <see cref="TradeStore"/>.
/// Soporta velas de tiempo, ticks, volumen, rango, delta y renko.
/// </summary>
public sealed class CandleSeries
{
    private readonly List<Candle> _candles = new();
    private int _processed; // cantidad de operaciones del store ya procesadas

    // estado para períodos no temporales
    private long _bucketEnd;           // fin (exclusivo) del bucket temporal actual, en ticks de DateTime
    private decimal _volumeInCandle;
    private long _renkoBase;           // cierre del último ladrillo (ticks)
    private int _renkoDir;

    public CandleSeries(Instrument instrument, ChartPeriod period, int priceStep = 1, SessionSettings? session = null)
    {
        Instrument = instrument;
        Period = period;
        PriceStep = Math.Max(1, priceStep);
        Session = session ?? SessionSettings.CryptoUtc;
    }

    public Instrument Instrument { get; }
    public ChartPeriod Period { get; }

    /// <summary>Ticks del instrumento por fila del footprint.</summary>
    public int PriceStep { get; }

    public SessionSettings Session { get; }
    public decimal CurrentOpenInterest { get; set; }
    
    public void UpdateOpenInterest(decimal oi)
    {
        CurrentOpenInterest = oi;
        if (_candles.Count > 0)
        {
            _candles[^1].OI = oi;
        }
    }

    /// <summary>Tamaño de fila en precio (tick efectivo).</summary>
    public decimal RowSize => Instrument.TickSize * PriceStep;

    public int Count => _candles.Count;
    public Candle this[int index] => _candles[index];
    public Candle? Last => _candles.Count > 0 ? _candles[^1] : null;
    public IReadOnlyList<Candle> Candles => _candles;

    /// <summary>Fila de un precio en ticks (división entera hacia abajo).</summary>
    public long RowOf(long priceTicks) => FloorDiv(priceTicks, PriceStep);

    /// <summary>Precio (límite inferior) de una fila.</summary>
    public decimal RowPrice(long row) => row * PriceStep * Instrument.TickSize;

    /// <summary>
    /// Centro visual de una fila: la fila cubre los ticks [row*paso, row*paso+paso-1], así que su centro
    /// es (row*paso + (paso-1)/2) ticks. Con paso 1 coincide con el precio de la fila.
    /// </summary>
    public decimal RowCenter(long row) => (row * PriceStep + (PriceStep - 1) / 2m) * Instrument.TickSize;

    /// <summary>True si el precio cae justo en el límite inferior de una fila (precio de nivel).</summary>
    public bool IsRowPrice(decimal price) => RowSize > 0 && price % RowSize == 0;

    public long PriceToRow(decimal price) => RowOf(Instrument.ToTicks(price));

    public decimal TicksToPrice(long ticks) => ticks * Instrument.TickSize;

    public static long FloorDiv(long a, long b)
    {
        long q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }

    public void Clear()
    {
        _candles.Clear();
        _processed = 0;
        _bucketEnd = 0;
        _volumeInCandle = 0;
        _renkoBase = 0;
        _renkoDir = 0;
    }

    /// <summary>Reconstruye todo desde el almacén.</summary>
    public void Rebuild(TradeStore store)
    {
        Clear();
        Append(store);
    }

    /// <summary>Procesa las operaciones nuevas del almacén.</summary>
    public AppendResult Append(TradeStore store)
    {
        if (_processed >= store.Count) return AppendResult.None;
        int firstChanged = _candles.Count > 0 ? _candles.Count - 1 : 0;
        int before = _candles.Count;

        for (int i = _processed; i < store.Count; i++)
        {
            ProcessTrade(store, i);
        }

        _processed = store.Count;
        int created = _candles.Count - before;
        if (_candles.Count == 0) return AppendResult.None;
        return new AppendResult(Math.Min(firstChanged, _candles.Count - 1), created);
    }

    private void ProcessTrade(TradeStore store, int i)
    {
        long timeTicks = store.GetTimeTicks(i);
        long price = store.GetPriceTicks(i);
        decimal volume = store.GetVolume(i);
        var side = store.GetSide(i);
        long row = RowOf(price);
        bool syn = store.GetId(i) < 0; // operación armada desde velas del exchange

        switch (Period.Type)
        {
            case PeriodType.Seconds:
            case PeriodType.Minutes:
            case PeriodType.Hours:
            case PeriodType.Days:
            case PeriodType.Weeks:
            case PeriodType.Months:
            {
                if (_candles.Count == 0 || timeTicks >= _bucketEnd)
                {
                    var (start, end) = TimeBucket(timeTicks);
                    _bucketEnd = end;
                    StartCandle(new DateTime(start, DateTimeKind.Utc));
                }
                else if (timeTicks < _candles[^1].Time.Ticks)
                {
                    // operación más vieja que la vela actual (datos fuera de orden): no se mete en la vela que no es
                    break;
                }

                _candles[^1].AddTrade(price, row, volume, side, timeTicks, i, syn);
                break;
            }

            case PeriodType.Tick:
            {
                int n = Math.Max(1, (int)Period.Value);
                if (_candles.Count == 0 || _candles[^1].Trades >= n) StartCandle(new DateTime(timeTicks, DateTimeKind.Utc));
                _candles[^1].AddTrade(price, row, volume, side, timeTicks, i, syn);
                break;
            }

            case PeriodType.Volume:
            {
                decimal cap = Math.Max(Instrument.VolumeStep, Period.Value);
                decimal remaining = volume;
                while (remaining > 0)
                {
                    if (_candles.Count == 0 || _volumeInCandle >= cap)
                    {
                        StartCandle(new DateTime(timeTicks, DateTimeKind.Utc));
                        _volumeInCandle = 0;
                    }

                    decimal part = Math.Min(remaining, cap - _volumeInCandle);
                    _candles[^1].AddTrade(price, row, part, side, timeTicks, i, syn);
                    _volumeInCandle += part;
                    remaining -= part;
                }

                break;
            }

            case PeriodType.Range:
            {
                long range = Math.Max(1, (long)Period.Value) * PriceStep;
                var last = _candles.Count > 0 ? _candles[^1] : null;
                if (last == null || Math.Max(last.HighTicks, price) - Math.Min(last.LowTicks, price) > range)
                    StartCandle(new DateTime(timeTicks, DateTimeKind.Utc));
                _candles[^1].AddTrade(price, row, volume, side, timeTicks, i, syn);
                break;
            }

            case PeriodType.Delta:
            {
                decimal n = Math.Max(Instrument.VolumeStep, Period.Value);
                var last = _candles.Count > 0 ? _candles[^1] : null;
                if (last == null || Math.Abs(last.Delta) >= n) StartCandle(new DateTime(timeTicks, DateTimeKind.Utc));
                _candles[^1].AddTrade(price, row, volume, side, timeTicks, i, syn);
                break;
            }

            case PeriodType.Renko:
            {
                long brick = Math.Max(1, (long)Period.Value) * PriceStep;
                if (_candles.Count == 0)
                {
                    _renkoBase = FloorDiv(price, brick) * brick;
                    _renkoDir = 0;
                    StartCandle(new DateTime(timeTicks, DateTimeKind.Utc));
                }
                else
                {
                    long up = _renkoDir >= 0 ? _renkoBase + brick : _renkoBase + 2 * brick;
                    long down = _renkoDir <= 0 ? _renkoBase - brick : _renkoBase - 2 * brick;
                    if (price >= up || price <= down)
                    {
                        var c = _candles[^1];
                        bool isUp = price >= up;
                        long close = isUp ? up : down;
                        c.OpenTicks = isUp ? close - brick : close + brick;
                        c.CloseTicks = close;
                        c.HighTicks = Math.Max(c.HighTicks, Math.Max(c.OpenTicks, close));
                        c.LowTicks = Math.Min(c.LowTicks, Math.Min(c.OpenTicks, close));
                        _renkoBase = close;
                        _renkoDir = isUp ? 1 : -1;
                        StartCandle(new DateTime(timeTicks, DateTimeKind.Utc));
                    }
                }

                _candles[^1].AddTrade(price, row, volume, side, timeTicks, i, syn);
                break;
            }
        }
    }

    private void StartCandle(DateTime time)
    {
        if (_candles.Count > 0) _candles[^1].IsClosed = true;
        _candles.Add(new Candle { Time = time, LastTime = time });
    }

    /// <summary>Calcula el inicio y fin (ticks de DateTime UTC) del bucket temporal que contiene t.</summary>
    public (long Start, long End) TimeBucket(long t)
    {
        long n = Math.Max(1, (long)Period.Value);
        switch (Period.Type)
        {
            case PeriodType.Seconds:
            {
                long len = n * TimeSpan.TicksPerSecond;
                long s = t / len * len;
                return (s, s + len);
            }
            case PeriodType.Minutes:
            {
                long len = n * TimeSpan.TicksPerMinute;
                long s = t / len * len;
                return (s, s + len);
            }
            case PeriodType.Hours:
            {
                long len = n * TimeSpan.TicksPerHour;
                long s = t / len * len;
                return (s, s + len);
            }
            case PeriodType.Days:
            {
                var date = Session.SessionDate(new DateTime(t, DateTimeKind.Utc));
                // agrupa de a n días desde una fecha ancla fija
                int dayNumber = date.DayNumber;
                int startDay = dayNumber - (int)(dayNumber % n);
                var startDate = DateOnly.FromDayNumber(startDay);
                long s = Session.SessionStartUtc(startDate).Ticks;
                long e = Session.SessionStartUtc(startDate.AddDays((int)n)).Ticks;
                return (s, e);
            }
            case PeriodType.Weeks:
            {
                var date = SessionSettings.WeekStart(Session.SessionDate(new DateTime(t, DateTimeKind.Utc)));
                long s = Session.SessionStartUtc(date).Ticks;
                long e = Session.SessionStartUtc(date.AddDays(7 * (int)n)).Ticks;
                return (s, e);
            }
            case PeriodType.Months:
            {
                var date = Session.SessionDate(new DateTime(t, DateTimeKind.Utc));
                var first = new DateOnly(date.Year, date.Month, 1);
                long s = Session.SessionStartUtc(first).Ticks;
                long e = Session.SessionStartUtc(first.AddMonths((int)n)).Ticks;
                return (s, e);
            }
            default:
                return (t, long.MaxValue);
        }
    }

    /// <summary>Índice de la vela que contiene un instante (o la anterior más cercana). -1 si no hay.</summary>
    public int FindBar(DateTime utc)
    {
        int lo = 0, hi = _candles.Count - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_candles[mid].Time <= utc)
            {
                ans = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }

        return ans;
    }

    /// <summary>
    /// Sugiere un paso de fila (ticks por fila) para que una vela típica tenga ~<paramref name="targetRows"/> filas.
    /// </summary>
    public static int SuggestPriceStep(TradeStore store, Instrument instrument, ChartPeriod period, int targetRows = 18)
    {
        if (store.Count < 50) return 1;
        var probe = new CandleSeries(instrument, period, 1);
        probe.Append(store);
        if (probe.Count < 2) return 1;
        var ranges = new List<long>();
        int from = Math.Max(0, probe.Count - 200);
        for (int i = from; i < probe.Count; i++)
        {
            var c = probe[i];
            ranges.Add(c.HighTicks - c.LowTicks + 1);
        }

        ranges.Sort();
        long median = ranges[ranges.Count / 2];
        long raw = Math.Max(1, median / Math.Max(1, targetRows));
        return NiceStep(raw);
    }

    public static int NiceStep(long raw)
    {
        int[] nice = { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 10000, 20000, 25000, 50000, 100000 };
        foreach (var n in nice)
            if (n >= raw) return n;
        return nice[^1];
    }
}
