using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using TradingHub.Rendering.Context;
using TradingHub.Rendering.Tools;

namespace TradingHub.Indicators.Native;

/// <summary>
/// Multi Volume Profile por Sesión (con composición AMT y proyección de POC sin testear).
///
/// - POC sin testear: el POC de cada perfil cerrado se proyecta hacia la derecha hasta que el precio lo toca.
/// - POC testeados: se pueden mostrar atenuados (opcional).
/// </summary>
[DisplayName("Multi Volume Profile por Sesión")]
[Category(IndicatorCategories.VolumeOrderFlow)]
[Description("Varios perfiles de volumen por sesión, semana o mes, con área de valor y POC proyectado hasta que el precio lo toca.")]
public class VP : Indicator
{
    public enum PeriodoPerfil
    {
        [Display(Name = "Día")] Dia,
        [Display(Name = "Semana (desde el lunes)")] Semana,
        [Display(Name = "Mes")] Mes
    }

    private const int PocLookback = 60;   // perfiles hacia atrás en los que se buscan POC sin testear

    // ==================== parámetros ====================
    private int _maxProfiles = 10;
    private decimal _valueAreaPercent = 70;
    private decimal _anchoPorcentaje = 30;
    private decimal _agrupacionPrecio = 20;
    private bool _componerPerfiles = true;
    private PeriodoPerfil _periodo = PeriodoPerfil.Dia;

    private bool _mostrarPocSinTestear = true;
    private int _maxPocSinTestear = 0;
    private bool _mostrarPocTesteados = false;
    private bool _mostrarEtiquetas = true;

    private Color _profileColor = Color.FromArgb(70, 70, 130, 190);
    private Color _vaColor = Color.FromArgb(130, 30, 144, 255);
    private Color _pocColor = Color.FromArgb(255, 255, 140, 0);

    private readonly RenderFont _font = new("Arial", 9);

    [Display(Name = "Componer sesiones en balance", GroupName = "Teoría de Subasta (AMT)", Order = 1)]
    public bool ComponerPerfiles
    {
        get => _componerPerfiles;
        set { _componerPerfiles = value; RecalculateValues(); }
    }

    [Display(Name = "Período del perfil", GroupName = "Configuración", Order = 1)]
    public PeriodoPerfil Periodo
    {
        get => _periodo;
        set { _periodo = value; RecalculateValues(); }
    }

    [Display(Name = "Cantidad de perfiles", GroupName = "Configuración", Order = 2)]
    [Range(1, 50)]
    public int MaxProfiles
    {
        get => _maxProfiles;
        set { _maxProfiles = Math.Clamp(value, 1, 50); RecalculateValues(); }
    }

    [Display(Name = "Agrupación de precio ($)", GroupName = "Configuración", Order = 3)]
    [Range(1, 500)]
    public decimal AgrupacionPrecio
    {
        get => _agrupacionPrecio;
        set { _agrupacionPrecio = value <= 0 ? 10 : value; RecalculateValues(); }
    }

    [Display(Name = "Área de Valor (%)", GroupName = "Configuración", Order = 4)]
    [Range(10, 100)]
    public decimal ValueAreaPercent
    {
        get => _valueAreaPercent;
        set { _valueAreaPercent = Math.Clamp(value, 10m, 100m); RecalculateValues(); }
    }

    [Display(Name = "Ancho del perfil (% sesión)", GroupName = "Visualización", Order = 5)]
    [Range(10, 80)]
    public decimal AnchoPorcentaje
    {
        get => _anchoPorcentaje;
        set { _anchoPorcentaje = Math.Clamp(value, 10m, 80m); RedrawChart(); }
    }

    [Display(Name = "Proyectar POC sin testear", GroupName = "POC sin testear", Order = 6)]
    public bool MostrarPocSinTestear
    {
        get => _mostrarPocSinTestear;
        set { _mostrarPocSinTestear = value; RecalculateValues(); }
    }

    [Display(Name = "Máx. POC sin testear (0 = todos)", GroupName = "POC sin testear", Order = 7)]
    [Range(0, 100)]
    public int MaxPocSinTestear
    {
        get => _maxPocSinTestear;
        set { _maxPocSinTestear = Math.Max(0, value); RecalculateValues(); }
    }

    [Display(Name = "Mostrar POC ya testeados (tenues)", GroupName = "POC sin testear", Order = 8)]
    public bool MostrarPocTesteados
    {
        get => _mostrarPocTesteados;
        set { _mostrarPocTesteados = value; RedrawChart(); }
    }

    [Display(Name = "Mostrar etiquetas", GroupName = "POC sin testear", Order = 9)]
    public bool MostrarEtiquetas
    {
        get => _mostrarEtiquetas;
        set { _mostrarEtiquetas = value; RedrawChart(); }
    }

    // ==================== datos internos ====================
    private class ProfileBlock
    {
        public DateTime StartDate { get; set; }
        public int StartBar { get; set; }
        public int EndBar { get; set; }
        public Dictionary<decimal, decimal> VolumeByPrice { get; } = new();
        public decimal TotalVolume { get; set; }
        public decimal PocPrice { get; set; }
        public decimal MaxPriceVolume { get; set; }
        public decimal Vah { get; set; }
        public decimal Val { get; set; }
    }

    private class PocLevel
    {
        public decimal Price;
        public int StartBar;        // última vela del perfil de origen
        public int TestedBar = -1;  // -1 = sin testear
    }

    private readonly object _sync = new();
    private readonly List<ProfileBlock> _rawSessions = new();
    private List<ProfileBlock> _displayProfiles = new();
    private List<PocLevel> _pocs = new();

    // máximos y mínimos por vela (para saber si un nivel fue testeado)
    private readonly List<decimal> _hi = new();
    private readonly List<decimal> _lo = new();

    // lo que la vela viva aportó en el cálculo anterior (se resta antes de volver a sumarla)
    private readonly Dictionary<decimal, decimal> _contrib = new();
    private int _contribBar = -1;

    public VP() : base(true)
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

    private decimal RoundPrice(decimal price)
    {
        if (_agrupacionPrecio <= 0) return price;
        return Math.Round(price / _agrupacionPrecio) * _agrupacionPrecio;
    }

    // Inicio del período al que pertenece una vela (la semana arranca el lunes)
    private DateTime PeriodStart(DateTime t)
    {
        switch (_periodo)
        {
            case PeriodoPerfil.Semana:
                return t.Date.AddDays(-(((int)t.DayOfWeek + 6) % 7));
            case PeriodoPerfil.Mes:
                return new DateTime(t.Year, t.Month, 1);
            default:
                return t.Date;
        }
    }

    // ==================== cálculo ====================
    protected override void OnRecalculate()
    {
        _rawSessions.Clear();
        _hi.Clear();
        _lo.Clear();
        _contrib.Clear();
        _contribBar = -1;
        lock (_sync)
        {
            _displayProfiles = new List<ProfileBlock>();
            _pocs = new List<PocLevel>();
        }
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar == 0)
        {
            // También se limpia acá para cuando el indicador se usa como calculadora (sin OnRecalculate).
            _rawSessions.Clear();
            _hi.Clear();
            _lo.Clear();
            _contrib.Clear();
            _contribBar = -1;
            lock (_sync)
            {
                _displayProfiles = new List<ProfileBlock>();
                _pocs = new List<PocLevel>();
            }
        }

        var candle = GetCandle(bar);

        while (_hi.Count <= bar) { _hi.Add(0); _lo.Add(0); }
        _hi[bar] = candle.High;
        _lo[bar] = candle.Low;

        var candleDate = PeriodStart(candle.Time);

        // vela viva: se recalcula en cada tick, así que primero se resta lo que ya había aportado
        if (bar == _contribBar && _rawSessions.Count > 0)
        {
            var s = _rawSessions.Last();
            foreach (var kv in _contrib)
            {
                if (s.VolumeByPrice.TryGetValue(kv.Key, out var cur))
                {
                    decimal nv = cur - kv.Value;
                    if (nv <= 0) s.VolumeByPrice.Remove(kv.Key);
                    else s.VolumeByPrice[kv.Key] = nv;
                    s.TotalVolume -= kv.Value;
                }
            }

            if (s.TotalVolume < 0) s.TotalVolume = 0;
        }

        _contrib.Clear();
        _contribBar = bar;

        ProfileBlock currentSession;

        if (_rawSessions.Count == 0 || _rawSessions.Last().StartDate != candleDate)
        {
            currentSession = new ProfileBlock
            {
                StartDate = candleDate,
                StartBar = bar,
                EndBar = bar
            };
            _rawSessions.Add(currentSession);
        }
        else
        {
            currentSession = _rawSessions.Last();
            currentSession.EndBar = bar;
        }

        var priceLevels = candle.GetAllPriceLevels()?.ToList();
        if (priceLevels != null && priceLevels.Count > 1)
        {
            foreach (var lvl in priceLevels)
                AddVolume(currentSession, RoundPrice(lvl.Price), lvl.Volume);
        }
        else
        {
            decimal low = RoundPrice(candle.Low);
            decimal high = RoundPrice(candle.High);
            decimal step = _agrupacionPrecio <= 0 ? 20 : _agrupacionPrecio;

            if (high <= low)
            {
                AddVolume(currentSession, low, candle.Volume);
            }
            else
            {
                int count = (int)Math.Max(1, Math.Round((high - low) / step) + 1);
                decimal volPerStep = candle.Volume / count;

                for (int i = 0; i < count; i++)
                    AddVolume(currentSession, low + (i * step), volPerStep);
            }
        }

        ComputeStats(currentSession);

        if (bar == CurrentBar - 1)
        {
            BuildCompositeProfiles();
        }
    }

    private void AddVolume(ProfileBlock session, decimal price, decimal vol)
    {
        if (vol <= 0) return;

        session.VolumeByPrice.TryGetValue(price, out var cur);
        session.VolumeByPrice[price] = cur + vol;
        session.TotalVolume += vol;

        _contrib.TryGetValue(price, out var c);
        _contrib[price] = c + vol;
    }

    private void ComputeStats(ProfileBlock block)
    {
        if (block.VolumeByPrice.Count == 0 || block.TotalVolume == 0) return;

        var poc = block.VolumeByPrice.OrderByDescending(kv => kv.Value).First();
        block.PocPrice = poc.Key;
        block.MaxPriceVolume = poc.Value;

        decimal vaTarget = block.TotalVolume * (_valueAreaPercent / 100m);
        decimal accumulated = poc.Value;

        var sortedPrices = block.VolumeByPrice.Keys.OrderBy(p => p).ToList();
        int pocIdx = sortedPrices.IndexOf(block.PocPrice);

        int up = pocIdx + 1;
        int down = pocIdx - 1;

        block.Vah = block.PocPrice;
        block.Val = block.PocPrice;

        while (accumulated < vaTarget && (up < sortedPrices.Count || down >= 0))
        {
            decimal volUp = up < sortedPrices.Count ? block.VolumeByPrice[sortedPrices[up]] : 0;
            decimal volDown = down >= 0 ? block.VolumeByPrice[sortedPrices[down]] : 0;

            if (volUp >= volDown && up < sortedPrices.Count)
            {
                accumulated += volUp;
                block.Vah = sortedPrices[up];
                up++;
            }
            else if (down >= 0)
            {
                accumulated += volDown;
                block.Val = sortedPrices[down];
                down--;
            }
            else if (up < sortedPrices.Count)
            {
                accumulated += volUp;
                block.Vah = sortedPrices[up];
                up++;
            }
        }
    }

    // Compone perfiles si el POC cae en el Value Area del composite actual (AMT)
    private void BuildCompositeProfiles()
    {
        var list = new List<ProfileBlock>();

        if (_rawSessions.Count > 0)
        {
            if (!_componerPerfiles)
            {
                foreach (var s in _rawSessions) list.Add(CloneBlock(s));
            }
            else
            {
                ProfileBlock currentComposite = null;

                foreach (var session in _rawSessions)
                {
                    if (currentComposite == null)
                    {
                        currentComposite = CloneBlock(session);
                        list.Add(currentComposite);
                        continue;
                    }

                    bool pocDentroDelValor = session.PocPrice >= currentComposite.Val && session.PocPrice <= currentComposite.Vah;

                    if (pocDentroDelValor)
                    {
                        currentComposite.EndBar = session.EndBar;

                        foreach (var kvp in session.VolumeByPrice)
                        {
                            currentComposite.VolumeByPrice.TryGetValue(kvp.Key, out var cur);
                            currentComposite.VolumeByPrice[kvp.Key] = cur + kvp.Value;
                            currentComposite.TotalVolume += kvp.Value;
                        }

                        ComputeStats(currentComposite);
                    }
                    else
                    {
                        currentComposite = CloneBlock(session);
                        list.Add(currentComposite);
                    }
                }
            }
        }

        var pocs = new List<PocLevel>();
        BuildPocs(list, pocs);

        lock (_sync)
        {
            _displayProfiles = list;
            _pocs = pocs;
        }
    }

    private ProfileBlock CloneBlock(ProfileBlock src)
    {
        var block = new ProfileBlock
        {
            StartDate = src.StartDate,
            StartBar = src.StartBar,
            EndBar = src.EndBar,
            TotalVolume = src.TotalVolume,
            PocPrice = src.PocPrice,
            MaxPriceVolume = src.MaxPriceVolume,
            Vah = src.Vah,
            Val = src.Val
        };

        foreach (var kv in src.VolumeByPrice)
            block.VolumeByPrice[kv.Key] = kv.Value;

        return block;
    }

    // ==================== POC sin testear ====================
    private int FindTouch(decimal lo, decimal hi, int fromBar, int toBar)
    {
        int end = Math.Min(toBar, _hi.Count - 1);
        for (int i = Math.Max(0, fromBar); i <= end; i++)
            if (_hi[i] >= lo && _lo[i] <= hi) return i;

        return -1;
    }

    private void BuildPocs(List<ProfileBlock> list, List<PocLevel> pocs)
    {
        int lastBar = Math.Min(CurrentBar - 1, _hi.Count - 1);
        if (list.Count == 0 || lastBar < 0) return;

        decimal price = GetCandle(lastBar).Close;

        // POC de los perfiles ya cerrados: sin testear mientras ninguna vela posterior lo toque
        if (_mostrarPocSinTestear)
        {
            int from = Math.Max(0, list.Count - 1 - PocLookback);
            var all = new List<PocLevel>();

            for (int k = from; k < list.Count - 1; k++)
            {
                var p = list[k];
                if (p.PocPrice <= 0 || p.VolumeByPrice.Count == 0) continue;

                all.Add(new PocLevel
                {
                    Price = p.PocPrice,
                    StartBar = p.EndBar,
                    TestedBar = FindTouch(p.PocPrice, p.PocPrice, p.EndBar + 1, lastBar)
                });
            }

            var untested = all.Where(x => x.TestedBar == -1).OrderBy(x => Math.Abs(x.Price - price));
            pocs.AddRange(_maxPocSinTestear > 0 ? untested.Take(_maxPocSinTestear) : untested);
            pocs.AddRange(all.Where(x => x.TestedBar != -1).OrderByDescending(x => x.TestedBar).Take(10));
        }
    }

    // ==================== dibujo ====================
    private static Color WithAlpha(Color c, double factor)
    {
        return Color.FromArgb(Math.Clamp((int)(c.A * factor), 0, 255), c.R, c.G, c.B);
    }

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        if (ChartInfo == null) return;

        List<ProfileBlock> profiles;
        List<PocLevel> pocs;
        lock (_sync)
        {
            profiles = _displayProfiles;
            pocs = _pocs;
        }

        if (profiles.Count == 0) return;

        int firstVisible = FirstVisibleBarNumber;
        int lastVisible = LastVisibleBarNumber;
        int xRight = ChartInfo.GetXByBar(lastVisible);

        // --- perfiles ---
        var visibleProfiles = profiles
            .Where(s => s.EndBar >= firstVisible && s.StartBar <= lastVisible)
            .TakeLast(_maxProfiles);

        var penPoc = new RenderPen(_pocColor, 2);

        foreach (var profile in visibleProfiles)
        {
            if (profile.VolumeByPrice.Count == 0 || profile.MaxPriceVolume == 0) continue;

            int startX = ChartInfo.GetXByBar(profile.StartBar);
            int endX = ChartInfo.GetXByBar(profile.EndBar);

            int widthRange = Math.Max(40, endX - startX);
            int maxWidth = Math.Max(30, (int)(widthRange * (_anchoPorcentaje / 100m)));

            var sortedPrices = profile.VolumeByPrice.Keys.OrderBy(p => p).ToList();

            for (int i = 0; i < sortedPrices.Count; i++)
            {
                decimal price = sortedPrices[i];
                decimal volume = profile.VolumeByPrice[price];

                int y = ChartInfo.GetYByPrice(price);
                int barWidth = (int)((volume / profile.MaxPriceVolume) * maxWidth);

                if (barWidth < 2) continue;

                decimal nextPrice = (i + 1 < sortedPrices.Count) ? sortedPrices[i + 1] : price + _agrupacionPrecio;
                int yNext = ChartInfo.GetYByPrice(nextPrice);

                int topY = Math.Min(y, yNext);
                int barHeight = Math.Max(2, Math.Abs(y - yNext) + 1);

                Color colorToUse = (price >= profile.Val && price <= profile.Vah) ? _vaColor : _profileColor;

                context.FillRectangle(colorToUse, new Rectangle(startX, topY, barWidth, barHeight));
            }

            int pocY = ChartInfo.GetYByPrice(profile.PocPrice);
            context.DrawLine(penPoc, startX, pocY, endX, pocY);
        }

        // --- POC proyectados hasta que el precio los toque ---
        foreach (var poc in pocs)
        {
            bool tested = poc.TestedBar != -1;
            if (tested && !_mostrarPocTesteados) continue;
            if (poc.StartBar > lastVisible) continue;
            if (tested && poc.TestedBar < firstVisible) continue;

            int x0 = Math.Max(ChartInfo.GetXByBar(poc.StartBar), ChartInfo.GetXByBar(firstVisible));
            int x1 = tested ? ChartInfo.GetXByBar(Math.Min(poc.TestedBar, lastVisible)) : xRight;
            if (x1 <= x0) continue;

            int y = ChartInfo.GetYByPrice(poc.Price);
            var pen = tested
                ? new RenderPen(WithAlpha(_pocColor, 0.35), 1f)
                : new RenderPen(WithAlpha(_pocColor, 0.9), 1.5f);

            context.DrawLine(pen, x0, y, x1, y);

            if (_mostrarEtiquetas && !tested)
            {
                string txt = "POC " + poc.Price.ToString("0", CultureInfo.InvariantCulture);
                context.DrawString(txt, _font, WithAlpha(_pocColor, 0.9), x0 + 4, y - 14);
            }
        }
    }
}
