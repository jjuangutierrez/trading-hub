using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using TradingHub.Indicators;
using TradingHub.Indicators.Drawing;

[DisplayName("Open Interest")]
[Category("Mis indicadores")]
public class OpenInterest : Indicator
{
    public enum ModoVisual
    {
        [Display(Name = "Variación (Barras Verde/Roja)")] BarrasDelta,
        [Display(Name = "Total (Línea continua)")] LineaTotal,
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly ValueDataSeries _sube = new("Sube", "OI Sube (+)")
    {
        Color = Color.FromArgb(255, 38, 166, 154), // Verde brillante
        VisualType = VisualMode.Histogram,
        Width = 2
    };

    private readonly ValueDataSeries _baja = new("Baja", "OI Baja (-)")
    {
        Color = Color.FromArgb(255, 239, 83, 80),  // Rojo brillante
        VisualType = VisualMode.Histogram,
        Width = 2
    };

    private readonly ValueDataSeries _linea = new("Total", "OI Total")
    {
        Color = Color.Orange,
        VisualType = VisualMode.Line,
        Width = 2
    };

    private ModoVisual _modo = ModoVisual.BarrasDelta;

    [Display(Name = "Modo de visualización", GroupName = "Visual")]
    public ModoVisual Modo
    {
        get => _modo;
        set
        {
            _modo = value;
            ActualizarVisibilidad();
            RecalculateValues();
        }
    }

    private readonly List<long> _tiempos = new();
    private readonly List<decimal> _valores = new();
    private readonly object _lock = new();
    private bool _cargando;
    private bool _cargado;
    private decimal _ultimo;

    public OpenInterest()
    {
        Panel = IndicatorDataProvider.NewPanel;
        DataSeries[0] = _sube;
        DataSeries.Add(_baja);
        DataSeries.Add(_linea);
        ActualizarVisibilidad();
    }

    private void ActualizarVisibilidad()
    {
        bool esBarras = _modo == ModoVisual.BarrasDelta;
        _sube.Visible = esBarras;
        _baja.Visible = esBarras;
        _linea.Visible = !esBarras;
    }

    private static long Ms(DateTime t)
    {
        var utc = t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
        return new DateTimeOffset(utc).ToUnixTimeMilliseconds();
    }

    private void CargarHistorial(string simboloRaw)
    {
        if (_cargando || _cargado) return;
        _cargando = true;

        string simbolo = simboloRaw.Replace("binance-futures:", "").Replace("binance-spot:", "").Replace("/", "").Split(' ')[0];
        if (string.IsNullOrWhiteSpace(simbolo)) simbolo = "BTCUSDT";

        Task.Run(async () =>
        {
            try
            {
                var todos = new Dictionary<long, decimal>();
                long hasta = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long limite = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeMilliseconds();

                for (int i = 0; i < 8 && hasta > limite; i++)
                {
                    var url = $"https://fapi.binance.com/futures/data/openInterestHist?symbol={simbolo}&period=5m&limit=500&endTime={hasta}";
                    var json = await Http.GetStringAsync(url);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.ValueKind != JsonValueKind.Array) break;
                    int n = doc.RootElement.GetArrayLength();
                    if (n == 0) break;

                    long primerTimestamp = long.MaxValue;

                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        long t = e.GetProperty("timestamp").GetInt64();
                        decimal v = decimal.Parse(e.GetProperty("sumOpenInterest").GetString()!, CultureInfo.InvariantCulture);
                        todos[t] = v;
                        if (t < primerTimestamp) primerTimestamp = t;
                    }

                    if (primerTimestamp >= hasta || primerTimestamp == long.MaxValue) break;
                    hasta = primerTimestamp - 1;

                    await Task.Delay(60);
                }

                lock (_lock)
                {
                    _tiempos.Clear();
                    _valores.Clear();
                    foreach (var kv in todos.OrderBy(k => k.Key))
                    {
                        _tiempos.Add(kv.Key);
                        _valores.Add(kv.Value);
                    }
                    _cargado = true;
                }

                RecalculateValues();
            }
            catch { }
            finally { _cargando = false; }
        });
    }

    private decimal Buscar(long t)
    {
        lock (_lock)
        {
            if (_tiempos.Count == 0) return 0;
            int lo = 0, hi = _tiempos.Count - 1, res = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_tiempos[mid] <= t) { res = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return res >= 0 ? _valores[res] : _valores[0];
        }
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == 0)
        {
            _ultimo = 0;
            if (!_cargado && !_cargando)
            {
                var sym = InstrumentInfo?.Instrument ?? "BTCUSDT";
                CargarHistorial(sym);
            }
        }

        var vela = GetCandle(bar);
        decimal oi = (bar == CurrentBar - 1 && vela.OI > 0) ? vela.OI : Buscar(Ms(vela.Time));

        decimal previo = _ultimo;
        if (oi > 0) _ultimo = oi;

        // 1. Serie de Línea Total
        _linea[bar] = _ultimo;

        // 2. Serie de Barras (Variación neta)
        if (previo > 0 && _ultimo > 0)
        {
            decimal deltaOi = _ultimo - previo;

            if (deltaOi > 0)
            {
                _sube[bar] = deltaOi;
                _baja[bar] = 0;
            }
            else if (deltaOi < 0)
            {
                _sube[bar] = 0;
                _baja[bar] = deltaOi;
            }
            else
            {
                _sube[bar] = 0;
                _baja[bar] = 0;
            }
        }
        else
        {
            _sube[bar] = 0;
            _baja[bar] = 0;
        }
    }
}