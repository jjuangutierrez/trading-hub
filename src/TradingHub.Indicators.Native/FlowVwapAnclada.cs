using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Indicators.Drawing;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Flow VWAP anclado (sin reinicio por sesión, pensado para BTC 24/7).
///
///   FlowVWAP = Σ(Premium·P) / ΣPremium
///   σ        = sqrt( Σ(Premium·(P − FlowVWAP)²) / ΣPremium )
///
/// Ancla (elige un modo):
///  - Manual: fecha/hora escrita a mano ("2026-10-24 14:00").
///  - Swing automático: último pivot (mínimo/máximo) confirmado con N velas a cada lado.
///  - Pico de volumen: última vela con volumen &gt;= X veces el promedio (el "latigazo").
///
/// Visual: línea + bandas ±kσ, relleno translúcido apilado (el centro se ve más oscuro)
/// y burbujas elípticas cuyo grosor depende de la intensidad del flujo (|delta|) de cada vela.
/// </summary>
[DisplayName("Flow VWAP Anclado")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("VWAP anclado a un swing, a un pico de volumen o a una fecha, con bandas de desvío y burbujas de flujo.")]
public class FlowVwapAnclada : Indicator
{
    public enum AnchorMode
    {
        [Display(Name = "Manual (fecha/hora)")] Manual,
        [Display(Name = "Swing automático")] Swing,
        [Display(Name = "Pico de volumen")] PicoVolumen
    }

    public enum PivotKind
    {
        [Display(Name = "Mínimo")] Minimo,
        [Display(Name = "Máximo")] Maximo,
        [Display(Name = "Ambos")] Ambos
    }

    public enum PremiumMode
    {
        [Display(Name = "Delta absoluto")] DeltaAbsoluto,
        [Display(Name = "Notional (precio × volumen)")] Notional,
        [Display(Name = "Volumen")] Volumen
    }

    // ==================== parámetros ====================
    private AnchorMode _modo = AnchorMode.Swing;
    private string _manual = "";
    private DateTime? _t0;
    private int _pivotLen = 10;
    private PivotKind _pivotTipo = PivotKind.Ambos;
    private int _volPeriodo = 50;
    private decimal _volMult = 3m;

    private PremiumMode _premium = PremiumMode.DeltaAbsoluto;
    private int _bandas = 3;
    private decimal _multiplicador = 1m;

    private int _opacidad = 22;
    private bool _verBurbujas = true;
    private int _burbujaMax = 14;
    private int _grosor = 2;
    private Color _colorVwap = Color.FromArgb(255, 255, 205, 80);
    private Color _colorBandas = Color.FromArgb(255, 240, 180, 40);

    [Display(Name = "Modo de ancla", GroupName = "Ancla", Order = 1)]
    public AnchorMode Modo
    {
        get => _modo;
        set { _modo = value; RecalculateValues(); }
    }

    [Display(Name = "Manual: fecha/hora (yyyy-MM-dd HH:mm)", GroupName = "Ancla", Order = 2)]
    public string Manual
    {
        get => _manual;
        set
        {
            _manual = value ?? "";
            _t0 = DateTime.TryParse(_manual, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTime?)null;
            RecalculateValues();
        }
    }

    [Display(Name = "Swing: velas a cada lado", GroupName = "Ancla", Order = 3)]
    [Range(2, 200)]
    public int PivotLen
    {
        get => _pivotLen;
        set { _pivotLen = Math.Max(2, value); RecalculateValues(); }
    }

    [Display(Name = "Swing: tipo de pivot", GroupName = "Ancla", Order = 4)]
    public PivotKind PivotTipo
    {
        get => _pivotTipo;
        set { _pivotTipo = value; RecalculateValues(); }
    }

    [Display(Name = "Pico: período del promedio", GroupName = "Ancla", Order = 5)]
    [Range(5, 500)]
    public int VolPeriodo
    {
        get => _volPeriodo;
        set { _volPeriodo = Math.Max(5, value); RecalculateValues(); }
    }

    [Display(Name = "Pico: múltiplo del promedio", GroupName = "Ancla", Order = 6)]
    [Range(1.1, 20)]
    public decimal VolMult
    {
        get => _volMult;
        set { _volMult = value <= 1 ? 3m : value; RecalculateValues(); }
    }

    [Display(Name = "Ponderación (Premium)", GroupName = "Cálculo", Order = 7)]
    public PremiumMode Premium
    {
        get => _premium;
        set { _premium = value; RecalculateValues(); }
    }

    [Display(Name = "Cantidad de bandas (0-3)", GroupName = "Bandas", Order = 8)]
    [Range(0, 3)]
    public int Bandas
    {
        get => _bandas;
        set { _bandas = Math.Clamp(value, 0, 3); ApplyVisibility(); RecalculateValues(); }
    }

    [Display(Name = "Multiplicador de desvío", GroupName = "Bandas", Order = 9)]
    [Range(0.1, 10)]
    public decimal Multiplicador
    {
        get => _multiplicador;
        set { _multiplicador = value <= 0 ? 1m : value; RecalculateValues(); }
    }

    [Display(Name = "Opacidad del relleno (0-80)", GroupName = "Visualización", Order = 10)]
    [Range(0, 80)]
    public int Opacidad
    {
        get => _opacidad;
        set { _opacidad = Math.Clamp(value, 0, 80); RedrawChart(); }
    }

    [Display(Name = "Mostrar burbujas de flujo", GroupName = "Visualización", Order = 11)]
    public bool VerBurbujas
    {
        get => _verBurbujas;
        set { _verBurbujas = value; RedrawChart(); }
    }

    [Display(Name = "Tamaño máximo de burbuja (px)", GroupName = "Visualización", Order = 12)]
    [Range(4, 60)]
    public int BurbujaMax
    {
        get => _burbujaMax;
        set { _burbujaMax = Math.Clamp(value, 4, 60); RedrawChart(); }
    }

    [Display(Name = "Grosor línea", GroupName = "Visualización", Order = 13)]
    [Range(1, 10)]
    public int Grosor
    {
        get => _grosor;
        set { _grosor = Math.Clamp(value, 1, 10); ApplyStyle(); RedrawChart(); }
    }

    [Display(Name = "Color Flow VWAP", GroupName = "Visualización", Order = 14)]
    public Color ColorVwap
    {
        get => _colorVwap;
        set { _colorVwap = value; ApplyStyle(); RedrawChart(); }
    }

    [Display(Name = "Color bandas / relleno", GroupName = "Visualización", Order = 15)]
    public Color ColorBandas
    {
        get => _colorBandas;
        set { _colorBandas = value; ApplyStyle(); RedrawChart(); }
    }

    // ==================== series ====================
    private readonly ValueDataSeries _vwap = new("FlowVwapAnc", "Flow VWAP");
    private readonly ValueDataSeries _up1 = new("Up1", "+1σ");
    private readonly ValueDataSeries _dn1 = new("Dn1", "-1σ");
    private readonly ValueDataSeries _up2 = new("Up2", "+2σ");
    private readonly ValueDataSeries _dn2 = new("Dn2", "-2σ");
    private readonly ValueDataSeries _up3 = new("Up3", "+3σ");
    private readonly ValueDataSeries _dn3 = new("Dn3", "-3σ");

    // ==================== estado ====================
    private struct State
    {
        public decimal W;     // Σ Premium
        public decimal WP;    // Σ Premium × P
        public decimal WP2;   // Σ Premium × P²
    }

    private struct Row
    {
        public int Anchor;        // vela de ancla vigente en esta vela (-1 = todavía sin ancla)
        public State S;
        public bool Ok;
        public decimal Vwap;
        public decimal Sigma;
        public decimal Flow;      // |delta| de la vela (intensidad de flujo)
    }

    private readonly object _sync = new();
    private readonly List<Row> _rows = new();

    public FlowVwapAnclada() : base(true)
    {
        Panel = IndicatorDataProvider.CandlesPanel;

        DataSeries[0] = _vwap;
        DataSeries.Add(_up1);
        DataSeries.Add(_dn1);
        DataSeries.Add(_up2);
        DataSeries.Add(_dn2);
        DataSeries.Add(_up3);
        DataSeries.Add(_dn3);

        EnableCustomDrawing = true;
        DrawAbovePrice = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);

        ApplyStyle();
        ApplyVisibility();
    }

    private void ApplyStyle()
    {
        _vwap.Color = _colorVwap;
        _vwap.Width = _grosor;

        var band = Color.FromArgb(110, _colorBandas.R, _colorBandas.G, _colorBandas.B);
        foreach (var s in new[] { _up1, _dn1, _up2, _dn2, _up3, _dn3 })
        {
            s.Color = band;
            s.Width = 1;
        }
    }

    private void ApplyVisibility()
    {
        _up1.IsHidden = _dn1.IsHidden = _bandas < 1;
        _up2.IsHidden = _dn2.IsHidden = _bandas < 2;
        _up3.IsHidden = _dn3.IsHidden = _bandas < 3;
    }

    // ==================== cálculo ====================
    private decimal Weight(decimal price, decimal volume, decimal ask, decimal bid) => _premium switch
    {
        PremiumMode.Notional => price * volume,
        PremiumMode.Volumen => volume,
        _ => Math.Abs(ask - bid)
    };

    private void AddBar(int i, ref State s)
    {
        var candle = GetCandle(i);

        bool real = false;
        try
        {
            var levels = candle.GetAllPriceLevels();
            if (levels != null)
            {
                int count = 0;
                decimal w = 0, wp = 0, wp2 = 0;
                foreach (var lvl in levels)
                {
                    count++;
                    decimal weight = Weight(lvl.Price, lvl.Volume, lvl.Ask, lvl.Bid);
                    w += weight;
                    wp += weight * lvl.Price;
                    wp2 += weight * lvl.Price * lvl.Price;
                }

                if (count > 1)
                {
                    real = true;
                    s.W += w;
                    s.WP += wp;
                    s.WP2 += wp2;
                }
            }
        }
        catch
        {
            real = false;
        }

        if (!real)
        {
            decimal p = (candle.High + candle.Low + candle.Close) / 3m;
            decimal weight = Weight(p, candle.Volume, candle.Ask, candle.Bid);
            s.W += weight;
            s.WP += weight * p;
            s.WP2 += weight * p * p;
        }
    }

    private Row Step(int i, int anchor, ref State s)
    {
        AddBar(i, ref s);

        var r = new Row { Anchor = anchor, S = s, Flow = Math.Abs(GetCandle(i).Delta) };
        if (s.W > 0)
        {
            decimal vwap = s.WP / s.W;
            decimal variance = s.WP2 / s.W - vwap * vwap;
            decimal sigma = variance > 0 ? (decimal)Math.Sqrt((double)variance) : 0m;

            r.Ok = true;
            r.Vwap = vwap;
            r.Sigma = sigma;
            WriteSeries(i, vwap, sigma * _multiplicador);
        }

        return r;
    }

    private void WriteSeries(int i, decimal vwap, decimal k)
    {
        _vwap[i] = vwap;
        if (_bandas >= 1) { _up1[i] = vwap + k; _dn1[i] = vwap - k; }
        if (_bandas >= 2) { _up2[i] = vwap + 2 * k; _dn2[i] = vwap - 2 * k; }
        if (_bandas >= 3) { _up3[i] = vwap + 3 * k; _dn3[i] = vwap - 3 * k; }
    }

    /// <summary>Devuelve la ancla vigente en esta vela (la anterior si no hay una nueva).</summary>
    private int DetectAnchor(int bar, int current)
    {
        switch (_modo)
        {
            case AnchorMode.Manual:
                if (current >= 0) return current;
                if (_t0 == null) return 0; // sin fecha válida: desde el inicio del historial
                return GetCandle(bar).Time >= _t0.Value ? bar : -1;

            case AnchorMode.Swing:
            {
                int i = bar - _pivotLen;
                if (i < _pivotLen || i == current) return current;

                var c = GetCandle(i);
                bool isLow = true, isHigh = true;
                for (int j = i - _pivotLen; j <= bar; j++)
                {
                    if (j == i) continue;
                    var cj = GetCandle(j);
                    if (cj.Low < c.Low) isLow = false;
                    if (cj.High > c.High) isHigh = false;
                    if (!isLow && !isHigh) break;
                }

                bool take = (_pivotTipo != PivotKind.Maximo && isLow) || (_pivotTipo != PivotKind.Minimo && isHigh);
                return take ? i : current;
            }

            default: // PicoVolumen
            {
                if (bar < _volPeriodo) return current;

                decimal sum = 0;
                for (int j = bar - _volPeriodo; j < bar; j++) sum += GetCandle(j).Volume;
                decimal avg = sum / _volPeriodo;

                return avg > 0 && GetCandle(bar).Volume >= avg * _volMult ? bar : current;
            }
        }
    }

    protected override void OnRecalculate()
    {
        lock (_sync) _rows.Clear();
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        lock (_sync)
        {
            if (bar == 0) _rows.Clear();

            // la vela viva se recalcula en cada tick: se descarta su estado previo
            if (_rows.Count > bar) _rows.RemoveRange(bar, _rows.Count - bar);
            while (_rows.Count < bar) _rows.Add(new Row { Anchor = -1 });

            int prevAnchor = bar > 0 ? _rows[bar - 1].Anchor : -1;
            int anchor = DetectAnchor(bar, prevAnchor);

            if (anchor < 0 || anchor > bar)
            {
                _rows.Add(new Row { Anchor = -1 });
                return;
            }

            var s = default(State);

            if (anchor != prevAnchor)
            {
                // ancla nueva: se recalcula el acumulado desde ella hasta la vela anterior
                for (int i = anchor; i < bar; i++)
                    _rows[i] = Step(i, anchor, ref s);
            }
            else
            {
                s = _rows[bar - 1].S;
            }

            _rows.Add(Step(bar, anchor, ref s));
        }
    }

    // ==================== dibujo ====================
    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;

        int first = Math.Max(FirstVisibleBarNumber, 0);
        int last = LastVisibleBarNumber;

        Row[] snap;
        int anchorBar;
        lock (_sync)
        {
            last = Math.Min(last, _rows.Count - 1);
            if (last < first) return;

            snap = new Row[last - first + 1];
            _rows.CopyTo(first, snap, 0, snap.Length);
            anchorBar = _rows[_rows.Count - 1].Anchor;
        }

        decimal maxFlow = 0;
        foreach (var r in snap)
            if (r.Ok && r.Flow > maxFlow) maxFlow = r.Flow;
        if (maxFlow <= 0) maxFlow = 1;

        var fill = _colorBandas;
        var core = _colorVwap;

        for (int n = 0; n < snap.Length; n++)
        {
            var r = snap[n];
            if (!r.Ok) continue;

            int bar = first + n;
            int x = ChartInfo.GetXByBar(bar);
            int xn = ChartInfo.GetXByBar(bar + 1);
            int w = Math.Max(3, Math.Abs(xn - x));
            int left = x - w / 2;

            decimal k = r.Sigma * _multiplicador;

            // relleno apilado: la banda interior se pinta encima de las exteriores (centro más oscuro)
            if (_opacidad > 0)
            {
                for (int b = _bandas; b >= 1; b--)
                {
                    int yTop = ChartInfo.GetYByPrice(r.Vwap + b * k);
                    int yBot = ChartInfo.GetYByPrice(r.Vwap - b * k);
                    int top = Math.Min(yTop, yBot);
                    int h = Math.Max(1, Math.Abs(yBot - yTop));
                    context.FillRectangle(Color.FromArgb(_opacidad, fill.R, fill.G, fill.B), new Rectangle(left, top, w + 1, h));
                }
            }

            if (!_verBurbujas) continue;

            // intensidad de flujo de la vela -> grosor y opacidad de la burbuja
            double t = Math.Min(1.0, (double)(r.Flow / maxFlow));
            int bh = (int)(3 + t * (_burbujaMax - 3));
            int alpha = (int)(60 + t * 180);
            var bubble = Color.FromArgb(alpha, core.R, core.G, core.B);

            // burbuja sobre el Flow VWAP
            DrawBubble(context, bubble, left, ChartInfo.GetYByPrice(r.Vwap), w, bh);

            // burbujas en la banda exterior cuando la vela la tocó
            if (_bandas >= 1)
            {
                var cd = GetCandle(bar);
                decimal up = r.Vwap + _bandas * k;
                decimal dn = r.Vwap - _bandas * k;
                if (cd.High >= up) DrawBubble(context, bubble, left, ChartInfo.GetYByPrice(up), w, bh);
                if (cd.Low <= dn) DrawBubble(context, bubble, left, ChartInfo.GetYByPrice(dn), w, bh);
            }
        }

        // marca de la vela de ancla
        if (anchorBar >= first && anchorBar <= last)
        {
            int xa = ChartInfo.GetXByBar(anchorBar);
            var pen = new RenderPen(Color.FromArgb(110, 255, 255, 255), 1);
            context.DrawLine(pen, xa, ChartArea.Top, xa, ChartArea.Bottom);
        }
    }

    private static void DrawBubble(RenderContext context, Color c, int left, int yCenter, int w, int h)
    {
        context.FillEllipse(c, new Rectangle(left, yCenter - h / 2, w + 2, h));
    }
}
