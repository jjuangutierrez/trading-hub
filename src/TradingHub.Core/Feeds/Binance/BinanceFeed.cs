using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TradingHub.Market;

namespace TradingHub.Feeds.Binance;

/// <summary>
/// Datos públicos de Binance (sin cuenta ni claves): instrumentos, operaciones agregadas
/// históricas por REST y, en vivo por WebSocket, el tape (aggTrade) y el libro (depth @100ms)
/// sincronizado con su foto inicial según el procedimiento oficial.
/// </summary>
public sealed class BinanceFeed : IMarketDataFeed, ITickerStatsProvider, IKlineHistory
{
    public const string FuturesId = "binance-futures";
    public const string SpotId = "binance-spot";

    private readonly bool _futures;
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _rate = new(1, 1);
    private int _usedWeight;
    private DateTime _weightMinute = DateTime.UtcNow;

    public BinanceFeed(bool futures)
    {
        _futures = futures;
        _http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("TradingHub/0.1");
    }

    public string Id => _futures ? FuturesId : SpotId;
    public string DisplayName => _futures ? "Binance Futures" : "Binance Spot";
    public bool SupportsHistory => true;
    public bool IsLive => true;

    private string RestBase => _futures ? "https://fapi.binance.com/fapi/v1" : "https://api.binance.com/api/v3";

    private IEnumerable<string> WsBases => _futures
        ? new[] { "wss://fstream.binance.com/market", "wss://fstream.binance.com" }
        : new[] { "wss://stream.binance.com:9443", "wss://data-stream.binance.vision" };

    private int AggTradesWeight => _futures ? 20 : 4;
    private int WeightLimit => _futures ? 2000 : 5000;
    private int DepthLimit => _futures ? 1000 : 5000;

    // ======================================================================= REST

    private async Task<JsonDocument> GetAsync(string pathAndQuery, int weight, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            await ThrottleAsync(weight, ct).ConfigureAwait(false);
            using var resp = await _http.GetAsync(RestBase + pathAndQuery, ct).ConfigureAwait(false);
            if (resp.Headers.TryGetValues("X-MBX-USED-WEIGHT-1M", out var vals) && int.TryParse(vals.FirstOrDefault(), out var w))
                _usedWeight = w;
            if ((int)resp.StatusCode is 429 or 418)
            {
                var retry = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                await Task.Delay(retry, ct).ConfigureAwait(false);
                continue;
            }

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (attempt < 2 && (int)resp.StatusCode >= 500)
                {
                    await Task.Delay(1000 * (attempt + 1), ct).ConfigureAwait(false);
                    continue;
                }

                throw new HttpRequestException($"Binance {(int)resp.StatusCode}: {body}");
            }

            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
    }

    /// <summary>Respeta el límite de peso por minuto de la API.</summary>
    private async Task ThrottleAsync(int weight, CancellationToken ct)
    {
        await _rate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            if (now.Minute != _weightMinute.Minute || (now - _weightMinute).TotalSeconds > 60)
            {
                _weightMinute = now;
                _usedWeight = 0;
            }

            if (_usedWeight + weight > WeightLimit)
            {
                var wait = TimeSpan.FromSeconds(61 - now.Second);
                await Task.Delay(wait, ct).ConfigureAwait(false);
                _usedWeight = 0;
                _weightMinute = DateTime.UtcNow;
            }

            _usedWeight += weight;
        }
        finally
        {
            _rate.Release();
        }
    }

    // ======================================================================= velas (historia larga)

    private static readonly HttpClient Archives = new() { Timeout = TimeSpan.FromMinutes(5) };

    private static long Ms(DateTime t) => new DateTimeOffset(DateTime.SpecifyKind(t, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public async Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct)
    {
        using var doc = await GetAsync($"/klines?symbol={instrument.Symbol}&interval=1d&startTime=0&limit=1", 2, ct).ConfigureAwait(false);
        if (doc.RootElement.GetArrayLength() == 0) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(doc.RootElement[0][0].GetInt64()).UtcDateTime;
    }

    /// <summary>
    /// Velas de 1, 5 o 15 minutos. Los meses completos salen del archivo público mensual
    /// (data.binance.vision: un año en segundos); el resto, por REST de a 1000/1500.
    /// </summary>
    public async Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        string interval = minutes switch { 5 => "5m", 15 => "15m", _ => "1m" };
        long step = minutes * 60_000L;
        long start = Ms(fromUtc) / step * step;
        long end = Ms(toUtc);
        double total = Math.Max(1, end - start);
        long cur = start;
        while (cur < end && !ct.IsCancellationRequested)
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(cur).UtcDateTime;
            var monthStart = new DateTime(dt.Year, dt.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var monthEnd = monthStart.AddMonths(1);
            long segEnd = Math.Min(end, Ms(monthEnd));

            // mes entero y ya publicado: archivo mensual
            if (cur == Ms(monthStart) && segEnd == Ms(monthEnd) && monthEnd <= DateTime.UtcNow.AddDays(-2))
            {
                var month = await DownloadMonthAsync(instrument.Symbol, interval, monthStart, ct).ConfigureAwait(false);
                if (month != null)
                {
                    if (month.Count > 0 && !onBatch(month)) return;
                    cur = segEnd;
                    progress?.Report((cur - start) / total);
                    continue;
                }
            }

            // por REST, de a páginas
            int limit = _futures ? 1500 : 1000;
            while (cur < segEnd && !ct.IsCancellationRequested)
            {
                using var doc = await GetAsync($"/klines?symbol={instrument.Symbol}&interval={interval}&startTime={cur}&endTime={segEnd - 1}&limit={limit}",
                    _futures ? 10 : 2, ct).ConfigureAwait(false);
                var arr = doc.RootElement;
                if (arr.GetArrayLength() == 0) break;
                var batch = new List<Kline>(arr.GetArrayLength());
                foreach (var k in arr.EnumerateArray())
                    batch.Add(new Kline(k[0].GetInt64(), Dec(k[1]), Dec(k[2]), Dec(k[3]), Dec(k[4]), Dec(k[5]), Dec(k[9]), true));
                if (!onBatch(batch)) return;
                long next = batch[^1].OpenTimeMs + step;
                if (next <= cur) break;
                cur = next;
                progress?.Report((cur - start) / total);
            }

            cur = Math.Max(cur, segEnd);
        }

        progress?.Report(1);
    }

    private async Task<List<Kline>?> DownloadMonthAsync(string symbol, string interval, DateTime month, CancellationToken ct)
    {
        string kind = _futures ? "futures/um" : "spot";
        string url = $"https://data.binance.vision/data/{kind}/monthly/klines/{symbol}/{interval}/{symbol}-{interval}-{month:yyyy-MM}.zip";
        try
        {
            using var resp = await Archives.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));
            if (entry == null) return null;
            using var reader = new StreamReader(entry.Open());
            return TradingHub.Data.KlineHistory.ParseBinanceCsv(reader);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // sin archivo: se usa REST
        }
    }

    public async Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct)
    {
        using var doc = await GetAsync("/exchangeInfo", _futures ? 1 : 20, ct).ConfigureAwait(false);
        var list = new List<Instrument>();
        foreach (var s in doc.RootElement.GetProperty("symbols").EnumerateArray())
        {
            string status = s.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
            if (status != "TRADING") continue;
            if (_futures && s.TryGetProperty("contractType", out var ctp) && ctp.GetString() != "PERPETUAL") continue;
            string symbol = s.GetProperty("symbol").GetString()!;
            decimal tick = 0.01m, step = 0.001m;
            foreach (var f in s.GetProperty("filters").EnumerateArray())
            {
                var ft = f.GetProperty("filterType").GetString();
                if (ft == "PRICE_FILTER") tick = Dec(f.GetProperty("tickSize"));
                else if (ft == "LOT_SIZE") step = Dec(f.GetProperty("stepSize"));
            }

            list.Add(new Instrument
            {
                Symbol = symbol,
                FeedId = Id,
                Exchange = DisplayName,
                TickSize = tick / 1.0000000000000000000000000000m,
                VolumeStep = step / 1.0000000000000000000000000000m,
                BaseAsset = s.GetProperty("baseAsset").GetString() ?? "",
                QuoteAsset = s.GetProperty("quoteAsset").GetString() ?? "",
                Description = $"{s.GetProperty("baseAsset").GetString()}/{s.GetProperty("quoteAsset").GetString()}{(_futures ? " perpetuo" : "")}",
            });
        }

        string[] popular = { "BTCUSDT", "ETHUSDT", "SOLUSDT", "BNBUSDT", "XRPUSDT", "DOGEUSDT", "BTCUSDC", "ETHUSDC" };
        return list.OrderBy(i => Array.IndexOf(popular, i.Symbol) is var k && k >= 0 ? k : 999).ThenBy(i => i.Symbol).ToList();
    }

    public async Task<IReadOnlyDictionary<string, TickerStats>> GetTickerStatsAsync(CancellationToken ct)
    {
        using var doc = await GetAsync("/ticker/24hr", _futures ? 40 : 80, ct).ConfigureAwait(false);
        var res = new Dictionary<string, TickerStats>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            string sym = t.GetProperty("symbol").GetString()!;
            res[sym] = new TickerStats(sym, Dec(t.GetProperty("lastPrice")), Dec(t.GetProperty("priceChangePercent")),
                Dec(t.GetProperty("quoteVolume")), Dec(t.GetProperty("highPrice")), Dec(t.GetProperty("lowPrice")));
        }

        return res;
    }

    private static decimal Dec(JsonElement e) => e.ValueKind == JsonValueKind.String
        ? decimal.Parse(e.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture)
        : e.GetDecimal();

    private static Trade ParseAggTrade(JsonElement t)
    {
        long id = t.GetProperty("a").GetInt64();
        decimal price = Dec(t.GetProperty("p"));
        decimal qty = Dec(t.GetProperty("q"));
        long time = t.GetProperty("T").GetInt64();
        bool buyerIsMaker = t.GetProperty("m").GetBoolean();
        return new Trade(id, DateTimeOffset.FromUnixTimeMilliseconds(time).UtcDateTime, price, qty, buyerIsMaker ? TradeSide.Sell : TradeSide.Buy);
    }

    public async Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct)
    {
        string sym = instrument.Symbol;
        long startMs = new DateTimeOffset(fromUtc).ToUnixTimeMilliseconds();
        long endMs = new DateTimeOffset(toUtc).ToUnixTimeMilliseconds();

        // id inicial: el primero a partir de fromUtc
        long nextId;
        if (fromId.HasValue) nextId = fromId.Value;
        else
        {
            using var first = await GetAsync($"/aggTrades?symbol={sym}&startTime={startMs}&endTime={Math.Min(endMs, startMs + 3_600_000)}&limit=1", AggTradesWeight, ct).ConfigureAwait(false);
            var arr = first.RootElement;
            if (arr.GetArrayLength() == 0)
            {
                // sin operaciones en la primera hora: arranca desde la última conocida
                using var last = await GetAsync($"/aggTrades?symbol={sym}&limit=1", AggTradesWeight, ct).ConfigureAwait(false);
                if (last.RootElement.GetArrayLength() == 0) return;
                nextId = last.RootElement[0].GetProperty("a").GetInt64();
            }
            else nextId = arr[0].GetProperty("a").GetInt64();
        }

        // id final aproximado (para el progreso)
        long lastId;
        using (var lastDoc = await GetAsync($"/aggTrades?symbol={sym}&limit=1", AggTradesWeight, ct).ConfigureAwait(false))
            lastId = lastDoc.RootElement.GetArrayLength() > 0 ? lastDoc.RootElement[0].GetProperty("a").GetInt64() : nextId;
        long firstId = nextId;

        while (!ct.IsCancellationRequested)
        {
            using var doc = await GetAsync($"/aggTrades?symbol={sym}&fromId={nextId}&limit=1000", AggTradesWeight, ct).ConfigureAwait(false);
            var arr = doc.RootElement;
            int n = arr.GetArrayLength();
            if (n == 0) break;
            var batch = new List<Trade>(n);
            bool done = false;
            foreach (var e in arr.EnumerateArray())
            {
                var t = ParseAggTrade(e);
                if (new DateTimeOffset(t.Time).ToUnixTimeMilliseconds() > endMs)
                {
                    done = true;
                    break;
                }

                batch.Add(t);
            }

            if (batch.Count > 0)
            {
                if (!onBatch(batch)) return;
                nextId = batch[^1].Id + 1;
                if (lastId > firstId) progress?.Report(Math.Clamp((nextId - firstId) / (double)(lastId - firstId), 0, 1));
            }

            if (done || n < 1000) break;
        }

        progress?.Report(1);
    }

    // ======================================================================= WebSocket

    public IDisposable Subscribe(Instrument instrument, IMarketDataSink sink)
    {
        var sub = new LiveSubscription(DisplayName, (st, msg) => sink.OnStatus(instrument, st, msg));
        string lower = instrument.Symbol.ToLowerInvariant();
        if (_futures)
        {
            // Binance Futures separó los canales: el tape va por /market y el libro por la ruta principal.
            // El libro llega cada 100 ms: si se corta 10 s, la conexión está muerta. La cinta puede tener
            // pausas largas en instrumentos poco líquidos; ahí avisan los pings.
            _ = Task.Run(() => RunAsync(instrument, sink, sub, "cinta", $"{lower}@aggTrade",
                new[] { "wss://fstream.binance.com/market", "wss://fstream.binance.com" }, TimeSpan.FromMinutes(5)));
            _ = Task.Run(() => RunAsync(instrument, sink, sub, "libro", $"{lower}@depth@100ms",
                new[] { "wss://fstream.binance.com", "wss://fstream.binance.com/public" }, TimeSpan.FromSeconds(10)));
            
            _ = Task.Run(() => PollOpenInterestAsync(instrument, sink, sub));
        }
        else
        {
            _ = Task.Run(() => RunAsync(instrument, sink, sub, "datos", $"{lower}@aggTrade/{lower}@depth@100ms", WsBases.ToArray(), TimeSpan.FromSeconds(10)));
        }

        return sub;
    }

    private async Task RunAsync(Instrument instrument, IMarketDataSink sink, LiveSubscription sub, string channel, string streams, string[] bases, TimeSpan stale)
    {
        var ct = sub.Token;
        long lastTradeId = -1;
        int baseIdx = 0;
        int failures = 0;
        int requested = 0;
        bool everConnected = false;

        while (!ct.IsCancellationRequested)
        {
            var url = $"{bases[baseIdx % bases.Length]}/stream?streams={streams}";
            using var ws = new ClientWebSocket();
            // pings propios cada 15 s: si el servidor no contesta en 10 s la conexión está muerta
            // (se cayó el Wi-Fi, la Mac durmió...) y se reconecta sola
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
            ws.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
            var conn = sub.BeginConnection();
            var book = new OrderBookBuilder();
            var buffer = new List<JsonElement>();
            long lastU = -1;
            bool synced = false;
            Task<JsonDocument>? snapshotTask = null;
            var docs = new List<JsonDocument>();
            DateTime lastPublish = DateTime.MinValue;
            bool receivedAny = false;
            string reason = "se cortó la conexión";

            try
            {
                if (!everConnected) sub.SetChannel(channel, FeedStatus.Connecting, "Conectando a " + DisplayName + "…");
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(conn.Token))
                {
                    connectCts.CancelAfter(TimeSpan.FromSeconds(15));
                    await ws.ConnectAsync(new Uri(url), connectCts.Token).ConfigureAwait(false);
                }

                // rellena el hueco si nos desconectamos (antes de leer el vivo, así no se desordena)
                if (lastTradeId >= 0)
                {
                    sub.SetChannel(channel, FeedStatus.Reconnecting, "Recuperando las operaciones del corte…");
                    lastTradeId = await BackfillAsync(instrument, lastTradeId + 1, sink, conn.Token).ConfigureAwait(false);
                }

                sub.SetChannel(channel, FeedStatus.Connected, null);
                everConnected = true;
                conn.CancelAfter(stale); // vigía: si no llega nada en este tiempo, se reconecta

                var recv = new byte[1 << 16];
                var ms = new MemoryStream();
                var tradeBatch = new List<Trade>();
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(recv, conn.Token).ConfigureAwait(false);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        ms.Write(recv, 0, r.Count);
                    } while (!r.EndOfMessage);

                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        reason = "el servidor cerró la conexión";
                        break;
                    }

                    receivedAny = true;
                    failures = 0;
                    conn.CancelAfter(stale);
                    ms.Position = 0;
                    var doc = JsonDocument.Parse(ms.ToArray());
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("data", out var data))
                    {
                        doc.Dispose();
                        continue;
                    }

                    string ev = data.TryGetProperty("e", out var e) ? e.GetString() ?? "" : "";
                    if (ev == "aggTrade")
                    {
                        var t = ParseAggTrade(data);
                        doc.Dispose();
                        if (t.Id <= lastTradeId) continue;
                        lastTradeId = t.Id;
                        tradeBatch.Clear();
                        tradeBatch.Add(t);
                        sink.OnTrades(instrument, tradeBatch.ToArray());
                        continue;
                    }

                    if (ev != "depthUpdate")
                    {
                        doc.Dispose();
                        continue;
                    }

                    // ---- libro: foto inicial + eventos en orden
                    if (!synced)
                    {
                        docs.Add(doc);
                        buffer.Add(data);
                        snapshotTask ??= GetAsync($"/depth?symbol={instrument.Symbol}&limit={DepthLimit}", _futures ? 20 : 250, conn.Token);
                        if (!snapshotTask.IsCompleted) continue;
                        using var snap = await snapshotTask.ConfigureAwait(false);
                        long lastUpdateId = snap.RootElement.GetProperty("lastUpdateId").GetInt64();
                        book.Clear();
                        foreach (var b in snap.RootElement.GetProperty("bids").EnumerateArray())
                            book.Set(BookSide.Bid, instrument.ToTicks(Dec(b[0])), Dec(b[1]));
                        foreach (var a in snap.RootElement.GetProperty("asks").EnumerateArray())
                            book.Set(BookSide.Ask, instrument.ToTicks(Dec(a[0])), Dec(a[1]));

                        bool first = true;
                        foreach (var d in buffer)
                        {
                            long U = d.GetProperty("U").GetInt64(), u = d.GetProperty("u").GetInt64();
                            if (_futures ? u < lastUpdateId : u <= lastUpdateId) continue;
                            if (first)
                            {
                                bool ok = _futures ? U <= lastUpdateId && u >= lastUpdateId : U <= lastUpdateId + 1 && u >= lastUpdateId + 1;
                                if (!ok) break;
                                first = false;
                            }

                            ApplyDepth(book, instrument, d);
                            lastU = u;
                        }

                        foreach (var dd in docs) dd.Dispose();
                        docs.Clear();
                        buffer.Clear();
                        snapshotTask = null;
                        if (lastU < 0) lastU = lastUpdateId;
                        synced = true;
                        sink.OnDepth(instrument, book.BuildUpdate(DateTime.UtcNow, 500, fullRefresh: true));
                        lastPublish = DateTime.UtcNow;
                        continue;
                    }

                    long U2 = data.GetProperty("U").GetInt64(), u2 = data.GetProperty("u").GetInt64();
                    bool inOrder = _futures
                        ? data.TryGetProperty("pu", out var pu) && pu.GetInt64() == lastU
                        : U2 == lastU + 1;
                    if (!inOrder && u2 > lastU)
                    {
                        // se perdió un evento: resincroniza
                        synced = false;
                        snapshotTask = null;
                        buffer.Clear();
                        docs.Add(doc);
                        buffer.Add(data);
                        continue;
                    }

                    if (u2 > lastU)
                    {
                        ApplyDepth(book, instrument, data);
                        lastU = u2;
                    }

                    doc.Dispose();
                    var now = DateTime.UtcNow;
                    if ((now - lastPublish).TotalMilliseconds >= 90)
                    {
                        sink.OnDepth(instrument, book.BuildUpdate(now, 500));
                        lastPublish = now;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                // el vigía (no llegó nada) o una reconexión pedida: se vuelve a conectar al toque
                reason = sub.WasRequested(ref requested) ? "reconexión pedida" : receivedAny ? "no llegaban datos" : "no respondió a tiempo";
                if (!receivedAny) failures++;
            }
            catch (Exception ex)
            {
                failures++;
                if (!receivedAny) baseIdx++; // prueba el otro servidor
                reason = ex.Message;
            }
            finally
            {
                foreach (var dd in docs) dd.Dispose();
                sub.EndConnection(conn);
            }

            if (ct.IsCancellationRequested) break;
            var wait = LiveSubscription.Backoff(failures);
            sub.SetChannel(channel, FeedStatus.Reconnecting, failures == 0
                ? $"Reconectando ({reason})…"
                : $"Reconectando en {wait.TotalSeconds:0} s · intento {failures} ({reason})");
            await Task.Delay(wait, ct).ContinueWith(_ => { }).ConfigureAwait(false);
        }

        sub.SetChannel(channel, FeedStatus.Disconnected, null);
    }

    private static void ApplyDepth(OrderBookBuilder book, Instrument instrument, JsonElement d)
    {
        foreach (var b in d.GetProperty("b").EnumerateArray())
            book.Set(BookSide.Bid, instrument.ToTicks(Dec(b[0])), Dec(b[1]));
        foreach (var a in d.GetProperty("a").EnumerateArray())
            book.Set(BookSide.Ask, instrument.ToTicks(Dec(a[0])), Dec(a[1]));
    }

    private async Task<long> BackfillAsync(Instrument instrument, long fromId, IMarketDataSink sink, CancellationToken ct)
    {
        long last = fromId - 1;
        try
        {
            await LoadTradesAsync(instrument, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddMinutes(1), fromId, batch =>
            {
                sink.OnTrades(instrument, batch);
                last = batch[^1].Id;
                return !ct.IsCancellationRequested;
            }, null, ct).ConfigureAwait(false);
        }
        catch
        {
            // el relleno es de mejor esfuerzo
        }

        return last;
    }
    
    private async Task PollOpenInterestAsync(Instrument instrument, IMarketDataSink sink, LiveSubscription sub)
    {
        while (!sub.Token.IsCancellationRequested)
        {
            try
            {
                // Consulta el endpoint REST oficial de Binance
                using var doc = await GetAsync($"/openInterest?symbol={instrument.Symbol}", 1, sub.Token).ConfigureAwait(false);
                decimal oi = Dec(doc.RootElement.GetProperty("openInterest"));

                // Se lo pasa al motor con la fecha y hora actual (3 parámetros)
                sink.OnOpenInterest(instrument, oi, DateTime.UtcNow); 
            }
            catch (OperationCanceledException) when (sub.Token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Ignora errores temporales de red y reintenta
            }

            try
            {
                // Espera 3 segundos antes de volver a consultar
                await Task.Delay(3000, sub.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
