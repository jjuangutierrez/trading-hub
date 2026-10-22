using TradingHub.Market;

namespace TradingHub.Feeds;

public enum FeedStatus
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Error,
}

/// <summary>Receptor de datos en vivo. Los feeds lo llaman desde SUS hilos.</summary>
public interface IMarketDataSink
{
    void OnTrades(Instrument instrument, IReadOnlyList<Trade> trades);
    void OnDepth(Instrument instrument, DepthUpdate update);
    void OnStatus(Instrument instrument, FeedStatus status, string? message);
    
    void OnOpenInterest(Instrument instrument, decimal openInterest, DateTime time)
    {
    }
}

/// <summary>Un proveedor de datos de mercado (exchange, simulador, replay...).</summary>
public interface IMarketDataFeed : IDisposable
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Si puede cargar operaciones históricas.</summary>
    bool SupportsHistory { get; }

    /// <summary>Si los datos vienen de un mercado real (para avisar en la UI).</summary>
    bool IsLive { get; }

    /// <summary>"Ahora" para este feed (el replay devuelve el reloj de la reproducción).</summary>
    DateTime Now => DateTime.UtcNow;

    Task<IReadOnlyList<Instrument>> GetInstrumentsAsync(CancellationToken ct);

    /// <summary>
    /// Carga operaciones históricas en orden cronológico, en lotes. Si <paramref name="fromId"/> está presente
    /// se usa para continuar desde una operación conocida (más preciso que el tiempo).
    /// <paramref name="onBatch"/> devuelve false para cortar la carga.
    /// </summary>
    Task LoadTradesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, long? fromId,
        Func<IReadOnlyList<Trade>, bool> onBatch, IProgress<double>? progress, CancellationToken ct);

    /// <summary>Suscribe a operaciones y profundidad en vivo. Dispose para desuscribir.</summary>
    IDisposable Subscribe(Instrument instrument, IMarketDataSink sink);
}

/// <summary>
/// Vela del exchange (1, 5 o 15 minutos) con el volumen comprador agresivo, para cargar historia larga
/// (meses o años) en segundos sin bajar cada operación.
/// </summary>
public readonly record struct Kline(long OpenTimeMs, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, decimal BuyVolume, bool HasBuyVolume);

/// <summary>Feeds que dan historia en velas (rápida). La usa la carga de historia para lo que no hay en ticks.</summary>
public interface IKlineHistory
{
    /// <summary>Primer instante con datos del instrumento (para «todo el historial»), o null si no se sabe.</summary>
    Task<DateTime?> FirstAvailableAsync(Instrument instrument, CancellationToken ct);

    /// <summary>
    /// Velas de <paramref name="minutes"/> minutos (1, 5 o 15) en orden cronológico, en lotes.
    /// <paramref name="endPrice"/> es el precio que sigue al rango (lo usa el simulador para empalmar).
    /// </summary>
    Task LoadKlinesAsync(Instrument instrument, DateTime fromUtc, DateTime toUtc, int minutes, decimal endPrice,
        Func<IReadOnlyList<Kline>, bool> onBatch, IProgress<double>? progress, CancellationToken ct);

    /// <summary>Etiqueta de la caché de velas: cambiarla descarta la caché vieja (por ejemplo si cambió cómo se arma).</summary>
    string CacheTag => "";
}

/// <summary>
/// Feeds con mercado que cierra (futuros): dicen cuándo fue la última operación, así con el mercado cerrado la
/// historia muestra la última sesión en vez de un gráfico vacío.
/// </summary>
public interface ISessionAwareFeed
{
    Task<DateTime?> LastTradeTimeAsync(Instrument instrument, CancellationToken ct);
}

/// <summary>Registro de feeds disponibles.</summary>
public sealed class FeedRegistry
{
    private readonly Dictionary<string, Func<IMarketDataFeed>> _factories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IMarketDataFeed> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Id, string Name)> _list = new();

    public void Register(string id, string displayName, Func<IMarketDataFeed> factory)
    {
        _factories[id] = factory;
        _list.Add((id, displayName));
    }

    public IReadOnlyList<(string Id, string Name)> Available => _list;

    public IMarketDataFeed Get(string id)
    {
        if (_instances.TryGetValue(id, out var f)) return f;
        if (!_factories.TryGetValue(id, out var factory)) throw new KeyNotFoundException($"Feed desconocido: {id}");
        f = factory();
        _instances[id] = f;
        return f;
    }

    public void DisposeAll()
    {
        foreach (var f in _instances.Values)
        {
            try { f.Dispose(); } catch { /* ignorar */ }
        }

        _instances.Clear();
    }
}

/// <summary>Estadísticas de 24 h de un instrumento (para ordenar y mostrar el selector).</summary>
public sealed record TickerStats(string Symbol, decimal LastPrice, decimal ChangePercent, decimal QuoteVolume, decimal High, decimal Low);

/// <summary>Feeds que pueden dar precio, variación y volumen de 24 h de todos sus instrumentos.</summary>
public interface ITickerStatsProvider
{
    Task<IReadOnlyDictionary<string, TickerStats>> GetTickerStatsAsync(CancellationToken ct);
}
