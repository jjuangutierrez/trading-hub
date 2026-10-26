using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Cluster Search: Refill Zones.
/// Detecta clústeres de precio con mucho volumen donde hubo absorción pasiva (icebergs / límites):
///  - Refill Comprador (verde): agresión vendedora en el Bid (Bid &gt; Ask y &gt;= 52% del volumen del clúster)
///    ubicada en el 35% inferior de la vela, con el cierre por encima del clúster (Close &gt; Mid).
///  - Refill Vendedor (rojo): agresión compradora en el Ask (Ask &gt; Bid y &gt;= 52% del volumen del clúster)
///    ubicada en el 35% superior de la vela, con el cierre por debajo del clúster (Close &lt; Mid).
///  - Neutral (ámbar): clúster de alto volumen sin condición de absorción (solo si FiltrarSoloAbsorcion = false).
///
/// Cada nivel de precio agrupado que cumple la condición es su propia zona: NO se fusionan clústeres
/// contiguos, así varias líneas apiladas se leen como un "peine" (absorción fuerte).
/// Las zonas se proyectan hacia la derecha hasta que una vela futura las toca (mitigación). Las mitigadas
/// siguen visibles (más tenues y finas) para conservar el contexto histórico.
/// Con footprint real usa los niveles de precio; en velas históricas sin niveles estima la distribución
/// del volumen entre el Low y el High a partir del Bid/Ask de la vela.
/// </summary>
[DisplayName("Cluster Search: Refill Zones")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Zonas de absorción pasiva (refill) en clústeres de volumen, proyectadas hasta que el precio las toca.")]
public class ClusterSearchRefill : Indicator
{
    // ==================== constantes de la lógica (ajustables aquí) ====================
    private const decimal ZoneEdgeShare = 0.35m;        // fracción de la vela (abajo/arriba) donde se busca absorción
    private const decimal DominantShare = 0.52m;        // % mínimo del volumen del clúster que debe ser del agresor
    private const decimal FallbackFactor = 0.4m;        // en velas sin footprint el volumen es estimado: umbral relajado
    private const int MaxEstimatedBuckets = 200;        // tope de clústeres estimados por vela

    // ==================== parámetros ====================
    private decimal _volumenMinimo = 50;
    private decimal _agrupacionPrecio = 10;
    private bool _filtrarSoloAbsorcion = true;
    private bool _usarEstimadas = true;
    private bool _extenderZonas = true;
    private bool _mostrarMitigadas = true;
    private int _vigenciaMax = 0;
    private Color _colorCompra = Color.FromArgb(46, 204, 113);   // verde esmeralda
    private Color _colorVenta = Color.FromArgb(220, 20, 60);     // rojo carmesí
    private Color _colorNeutro = Color.FromArgb(241, 196, 15);   // ámbar

    [Display(Name = "Volumen mínimo del clúster", GroupName = "Filtro de volumen", Order = 1)]
    [Range(1, 1000000)]
    public decimal VolumenMinimo
    {
        get => _volumenMinimo;
        set { _volumenMinimo = value <= 0 ? 1 : value; RecalculateValues(); }
    }

    [Display(Name = "Agrupación de precio", GroupName = "Filtro de volumen", Order = 2)]
    [Range(0.01, 10000)]
    public decimal AgrupacionPrecio
    {
        get => _agrupacionPrecio;
        set { _agrupacionPrecio = value <= 0 ? 1 : value; RecalculateValues(); }
    }

    [Display(Name = "Solo absorción (filtrar neutrales)", GroupName = "Filtro de absorción", Order = 3)]
    public bool FiltrarSoloAbsorcion
    {
        get => _filtrarSoloAbsorcion;
        set { _filtrarSoloAbsorcion = value; RecalculateValues(); }
    }

    [Display(Name = "Usar velas sin footprint (volumen estimado)", GroupName = "Filtro de absorción", Order = 4)]
    public bool UsarEstimadas
    {
        get => _usarEstimadas;
        set { _usarEstimadas = value; RecalculateValues(); }
    }

    [Display(Name = "Extender zonas hasta mitigación", GroupName = "Visualización", Order = 5)]
    public bool ExtenderZonas
    {
        get => _extenderZonas;
        set { _extenderZonas = value; RecalculateValues(); }
    }

    [Display(Name = "Mostrar zonas mitigadas (tenues)", GroupName = "Visualización", Order = 6)]
    public bool MostrarMitigadas
    {
        get => _mostrarMitigadas;
        set { _mostrarMitigadas = value; RedrawChart(); }
    }

    [Display(Name = "Vigencia máxima de la proyección (velas, 0 = sin límite)", GroupName = "Visualización", Order = 7)]
    [Range(0, 1000000)]
    public int VigenciaMax
    {
        get => _vigenciaMax;
        set { _vigenciaMax = Math.Max(0, value); RedrawChart(); }
    }

    [Display(Name = "Color refill comprador", GroupName = "Colores", Order = 8)]
    public Color ColorCompra
    {
        get => _colorCompra;
        set { _colorCompra = value; RedrawChart(); }
    }

    [Display(Name = "Color refill vendedor", GroupName = "Colores", Order = 9)]
    public Color ColorVenta
    {
        get => _colorVenta;
        set { _colorVenta = value; RedrawChart(); }
    }

    [Display(Name = "Color neutral", GroupName = "Colores", Order = 10)]
    public Color ColorNeutro
    {
        get => _colorNeutro;
        set { _colorNeutro = value; RedrawChart(); }
    }

    // ==================== datos internos ====================
    private enum RefillType
    {
        Comprador,
        Vendedor,
        Neutral
    }

    private sealed class RefillZone
    {
        public int BarIndex;
        public decimal Low;
        public decimal High;
        public RefillType Type;
        public bool Estimated;    // true = vela sin footprint (volumen repartido)
        public int EndBar = -1;   // -1 = sin testear todavía
    }

    private struct LevelAgg
    {
        public decimal Vol;
        public decimal Bid;   // volumen vendedor agresivo
        public decimal Ask;   // volumen comprador agresivo
    }

    private readonly object _sync = new();
    private readonly List<RefillZone> _zones = new();     // ordenadas por BarIndex
    private readonly List<RefillZone> _active = new();    // sin mitigar (también ordenadas por BarIndex)
    private readonly List<RefillZone> _visible = new();   // copia para dibujar fuera del lock
    private readonly Dictionary<long, LevelAgg> _buckets = new();

    public ClusterSearchRefill() : base(true)
    {
        if (DataSeries[0] is ValueDataSeries main)
        {
            main.VisualType = VisualMode.Hide;
            main.IsHidden = true;
        }

        EnableCustomDrawing = true;
        DrawAbovePrice = false;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
    }

    protected override void OnRecalculate()
    {
        lock (_sync)
        {
            _zones.Clear();
            _active.Clear();
            _visible.Clear();
        }
    }

    // ==================== cálculo ====================
    protected override void OnCalculate(int bar, decimal value)
    {
        var candle = GetCandle(bar);
        decimal step = _agrupacionPrecio > 0 ? _agrupacionPrecio : 1m;
        decimal high = candle.High;
        decimal low = candle.Low;
        decimal close = candle.Close;

        // 1) Agrupar el volumen por clúster de precio
        _buckets.Clear();
        bool real = false;
        try
        {
            var levels = candle.GetAllPriceLevels();
            if (levels != null)
            {
                int count = 0;
                foreach (var lvl in levels)
                {
                    count++;
                    long key = (long)Math.Floor(lvl.Price / step);
                    _buckets.TryGetValue(key, out var agg);
                    agg.Vol += lvl.Volume;
                    agg.Bid += lvl.Bid;
                    agg.Ask += lvl.Ask;
                    _buckets[key] = agg;
                }

                real = count > 1;
            }
        }
        catch
        {
            real = false;
        }

        decimal gridStep = step;
        decimal minVol = _volumenMinimo;
        if (!real)
        {
            // Fallback: vela histórica sin niveles. Se reparte el volumen entre Low y High
            // y el Bid/Ask de la vela proporcionalmente a cada clúster.
            _buckets.Clear();
            if (_usarEstimadas)
            {
                decimal range = high - low;
                if (range > 0 && range / step > MaxEstimatedBuckets) gridStep = range / MaxEstimatedBuckets;
                FillEstimated(low, high, gridStep, candle.Volume, candle.Bid, candle.Ask);
            }

            minVol = _volumenMinimo * FallbackFactor;
        }

        // 2) Actualizar zonas (todo bajo lock: el dibujo corre en otro hilo)
        lock (_sync)
        {
            // limpia lo de esta vela (y posteriores) para no duplicar en cada tick de la vela viva
            while (_zones.Count > 0 && _zones[_zones.Count - 1].BarIndex >= bar) _zones.RemoveAt(_zones.Count - 1);
            while (_active.Count > 0 && _active[_active.Count - 1].BarIndex >= bar) _active.RemoveAt(_active.Count - 1);

            // mitigación: ¿esta vela volvió a tocar zonas anteriores?
            if (_extenderZonas && _active.Count > 0)
            {
                int w = 0;
                for (int i = 0; i < _active.Count; i++)
                {
                    var z = _active[i];
                    bool touched = z.Type switch
                    {
                        RefillType.Comprador => low <= z.High,
                        RefillType.Vendedor => high >= z.Low,
                        _ => high >= z.Low && low <= z.High
                    };

                    if (touched) z.EndBar = bar;
                    else _active[w++] = z;
                }

                if (w < _active.Count) _active.RemoveRange(w, _active.Count - w);
            }

            // zonas nuevas de esta vela: UNA por cada clúster que cumple la condición (sin fusionar)
            foreach (var kv in _buckets)
            {
                var agg = kv.Value;
                if (agg.Vol < minVol) continue;

                decimal zLow = kv.Key * gridStep;
                decimal zHigh = zLow + gridStep;
                var type = Classify(zLow, zHigh, low, high, close, agg);

                if (type == RefillType.Neutral && _filtrarSoloAbsorcion) continue;

                var zone = new RefillZone
                {
                    BarIndex = bar,
                    Low = zLow,
                    High = zHigh,
                    Type = type,
                    Estimated = !real
                };

                _zones.Add(zone);
                if (_extenderZonas) _active.Add(zone);
            }
        }
    }

    private void FillEstimated(decimal low, decimal high, decimal g, decimal volume, decimal bid, decimal ask)
    {
        if (volume <= 0) return;

        decimal range = high - low;
        long k0 = (long)Math.Floor(low / g);
        long k1 = (long)Math.Floor(high / g);

        if (range <= 0 || k0 == k1)
        {
            _buckets[k0] = new LevelAgg { Vol = volume, Bid = bid, Ask = ask };
            return;
        }

        for (long k = k0; k <= k1; k++)
        {
            decimal bl = Math.Max(low, k * g);
            decimal bh = Math.Min(high, (k + 1) * g);
            decimal share = (bh - bl) / range;
            if (share <= 0) continue;
            _buckets[k] = new LevelAgg { Vol = volume * share, Bid = bid * share, Ask = ask * share };
        }
    }

    private static RefillType Classify(decimal zLow, decimal zHigh, decimal low, decimal high, decimal close, LevelAgg a)
    {
        decimal range = high - low;
        decimal mid = (zLow + zHigh) / 2m;
        decimal lowerLimit = low + range * ZoneEdgeShare;
        decimal upperLimit = high - range * ZoneEdgeShare;

        bool sellersAggressive = a.Bid > a.Ask && a.Bid >= a.Vol * DominantShare;
        bool buyersAggressive = a.Ask > a.Bid && a.Ask >= a.Vol * DominantShare;

        // Refill Comprador: abajo de la vela, ventas agresivas absorbidas, el precio cerró por encima del clúster
        if (sellersAggressive && zLow <= lowerLimit && close > mid) return RefillType.Comprador;

        // Refill Vendedor: arriba de la vela, compras agresivas frenadas, el precio cerró por debajo del clúster
        if (buyersAggressive && zHigh >= upperLimit && close < mid) return RefillType.Vendedor;

        return RefillType.Neutral;
    }

    // ==================== dibujo ====================
    private Color BaseColor(RefillType t) => t switch
    {
        RefillType.Comprador => _colorCompra,
        RefillType.Vendedor => _colorVenta,
        _ => _colorNeutro
    };

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null || CurrentBar <= 0) return;

        int first = FirstVisibleBarNumber;
        int last = LastVisibleBarNumber;
        bool extend = _extenderZonas;
        int curBar = CurrentBar - 1;
        int vigencia = _vigenciaMax;

        RefillZone[] snapshot;
        lock (_sync)
        {
            _visible.Clear();
            for (int i = 0; i < _zones.Count; i++)
            {
                var z = _zones[i];
                if (z.BarIndex > last) break; // ordenadas por vela

                bool mitigated = z.EndBar != -1;
                if (mitigated && !_mostrarMitigadas) continue;

                if (extend)
                {
                    // última vela hasta donde se proyecta la zona
                    int zoneEnd = mitigated ? z.EndBar : curBar;
                    if (vigencia > 0) zoneEnd = Math.Min(zoneEnd, z.BarIndex + vigencia);
                    if (zoneEnd < first) continue;
                }
                else if (z.BarIndex < first) continue;

                _visible.Add(z);
            }

            snapshot = _visible.ToArray();
        }

        if (snapshot.Length == 0) return;

        int lastBar = Math.Min(last, curBar);
        int leftLimit = ChartArea.Width > 0 ? ChartArea.Left : int.MinValue;
        bool cullY = ChartArea.Height > 0;

        foreach (var z in snapshot)
        {
            var c = BaseColor(z.Type);

            int yA = ChartInfo.GetYByPrice(z.High);
            int yB = ChartInfo.GetYByPrice(z.Low);
            int top = Math.Min(yA, yB);
            int height = Math.Max(3, Math.Abs(yB - yA));

            // fuera del rango de precios visible: no se dibuja
            if (cullY && (top > ChartArea.Bottom || top + height < ChartArea.Top)) continue;

            bool mitigated = z.EndBar != -1;

            // activas: opacidad completa; mitigadas: más tenues y finas; estimadas: algo más suaves
            double fade = (mitigated ? 0.45 : 1.0) * (z.Estimated ? 0.8 : 1.0);
            int markerAlpha = (int)(170 * fade);
            int borderAlpha = (int)(240 * fade);
            int fillAlpha = (int)(28 * fade);
            int lineAlpha = (int)(150 * fade);
            float borderWidth = mitigated ? 1f : 1.5f;
            float lineWidth = mitigated ? 1f : 1.5f;

            int xStart = ChartInfo.GetXByBar(z.BarIndex);
            int spacing = Math.Abs(ChartInfo.GetXByBar(z.BarIndex + 1) - xStart);
            int width = Math.Clamp(spacing, 6, 20);

            // marcador en la vela donde ocurrió
            if (z.BarIndex >= first)
            {
                var rect = new Rectangle(xStart - width / 2, top, width, height);
                context.FillRectangle(Color.FromArgb(markerAlpha, c.R, c.G, c.B), rect);
                var borderPen = new RenderPen(Color.FromArgb(borderAlpha, c.R, c.G, c.B), borderWidth);
                context.DrawRectangle(borderPen, rect);
            }

            // proyección hacia la derecha hasta la mitigación (o la última vela visible)
            if (extend)
            {
                int endBar = mitigated ? Math.Min(z.EndBar, last) : lastBar;
                if (vigencia > 0) endBar = Math.Min(endBar, z.BarIndex + vigencia);
                if (endBar <= z.BarIndex) continue;

                int xFrom = Math.Max(xStart + width / 2, leftLimit);
                int xEnd = ChartInfo.GetXByBar(endBar);
                if (xEnd <= xFrom) continue;

                context.FillRectangle(Color.FromArgb(fillAlpha, c.R, c.G, c.B), new Rectangle(xFrom, top, xEnd - xFrom, height));
                int yMid = ChartInfo.GetYByPrice((z.Low + z.High) / 2m);
                var linePen = new RenderPen(Color.FromArgb(lineAlpha, c.R, c.G, c.B), lineWidth);
                context.DrawLine(linePen, xFrom, yMid, xEnd, yMid);
            }
        }
    }
}
