using System.Drawing;
using SkiaSharp;
using TradingHub.Indicators;

namespace TradingHub.Charting;

/// <summary>Estado de navegación del gráfico (zoom horizontal, desplazamiento, escala de precios).</summary>
public sealed class ChartViewport
{
    /// <summary>Ancho de cada barra en píxeles (incluye el espacio entre barras).</summary>
    public double BarWidth { get; set; } = 90;

    /// <summary>Cuántas barras está desplazado hacia atrás respecto del borde derecho (0 = sigue el vivo).</summary>
    public double ScrollBars { get; set; }

    /// <summary>Espacio vacío a la derecha de la última barra, en píxeles.</summary>
    public double RightMarginPx { get; set; } = 70;

    /// <summary>Barras vacías a la derecha de la última (derivado de RightMarginPx).</summary>
    public int RightMarginBars => Math.Max(1, (int)Math.Ceiling(RightMarginPx / Math.Max(1, BarWidth)));

    /// <summary>Escala de precios automática.</summary>
    public bool AutoScale { get; set; } = true;

    /// <summary>Precios de los bordes cuando la escala es manual.</summary>
    public double PriceTop { get; set; }
    public double PriceBottom { get; set; }

    public const double MinBarWidth = 0.1;
    public const double MaxBarWidth = 400;

    public int BarSpacing => BarWidth >= 40 ? Math.Max(3, (int)(BarWidth * 0.06)) : BarWidth >= 5 ? Math.Max(1, (int)(BarWidth * 0.22)) : 0;

    public ChartViewport Clone() => (ChartViewport)MemberwiseClone();
}

/// <summary>Distribución del gráfico calculada en cada cuadro.</summary>
public sealed class ChartLayout
{
    public const float PriceAxisWidth = 78;
    public const float TimeAxisHeight = 24;

    public SKRect Bounds { get; init; }
    public SKRect Plot { get; init; }
    public SKRect PriceAxis { get; init; }
    public SKRect TimeAxis { get; init; }
    public List<PanelLayout> Panels { get; } = new();
    public PanelLayout Main => Panels[0];

    public int Count { get; init; }
    public double BarWidth { get; init; }
    public int BarSpacing { get; init; }
    public double ScrollBars { get; init; }
    public int RightMarginBars { get; init; }

    public int FirstVisible { get; set; }
    public int LastVisible { get; set; }

    /// <summary>Filas base agrupadas por fila visible del footprint (lo decide el renderer según el zoom).</summary>
    public int FootprintRowFactor { get; set; } = 1;

    /// <summary>Centro X de una barra (puede estar fuera de la pantalla o en el futuro).</summary>
    public float CenterX(int bar) =>
        (float)(Plot.Right - (Count - 1 - bar + RightMarginBars - ScrollBars + 0.5) * BarWidth);

    public float CenterX(double bar) =>
        (float)(Plot.Right - (Count - 1 - bar + RightMarginBars - ScrollBars + 0.5) * BarWidth);

    /// <summary>Borde izquierdo del espacio de una barra.</summary>
    public float LeftX(int bar) => CenterX(bar) - (float)(BarWidth / 2);

    /// <summary>Barra bajo una coordenada X (puede ser &lt; 0 o &gt;= Count).</summary>
    public int BarAt(float x) => (int)Math.Floor(BarAtExact(x) + 0.5);

    public double BarAtExact(float x) => Count - 1 + RightMarginBars - ScrollBars + 0.5 - (Plot.Right - x) / BarWidth;

    public PanelLayout? PanelAt(float y) => Panels.FirstOrDefault(p => y >= p.Rect.Top && y <= p.Rect.Bottom);
}

/// <summary>Un panel (el de precio o uno de indicadores) con su escala vertical.</summary>
public sealed class PanelLayout : IChartContainer
{
    public PanelLayout(ChartLayout chart, SKRect rect, bool isPrice)
    {
        Chart = chart;
        Rect = rect;
        IsPrice = isPrice;
    }

    public ChartLayout Chart { get; }
    public SKRect Rect { get; }
    public bool IsPrice { get; }

    /// <summary>Valor en el borde superior / inferior.</summary>
    public double Top { get; set; } = 1;
    public double Bottom { get; set; }

    /// <summary>Tamaño de fila (panel de precio).</summary>
    public decimal RowSize { get; set; } = 1m;

    /// <summary>Panel de indicadores (null en el de precio).</summary>
    public SubPanel? SubPanel { get; set; }

    public double Range => Top - Bottom == 0 ? 1 : Top - Bottom;

    public float Y(double value) => (float)(Rect.Top + (Top - value) / Range * Rect.Height);

    public float Y(decimal value) => Y((double)value);

    public double ValueAt(float y) => Top - (y - Rect.Top) / Rect.Height * Range;

    /// <summary>Alto en píxeles de una fila de precio.</summary>
    public double RowHeight => (double)RowSize / Range * Rect.Height;

    // ------------------------------------------------------------- IChartContainer (para indicadores)

    Rectangle IChartContainer.Region => new((int)Rect.Left, (int)Rect.Top, (int)Rect.Width, (int)Rect.Height);
    public Rectangle RelativeRegion => new(0, 0, (int)Rect.Width, (int)Rect.Height);
    public int BarsWidth => Math.Max(1, (int)Math.Round(Chart.BarWidth - Chart.BarSpacing));
    public int BarSpacing => Chart.BarSpacing;
    public decimal Step => RowSize;
    public decimal PriceRowHeight => (decimal)Math.Max(0.0001, RowHeight);
    public int TotalBars => Chart.Count;
    public decimal High => (decimal)Top;
    public decimal Low => (decimal)Bottom;
    public decimal Minimum => (decimal)Bottom;
    public decimal Maximum => (decimal)Top;
    public int FirstVisibleBarNumber => Chart.FirstVisible;
    public int LastVisibleBarNumber => Chart.LastVisible;

    public int GetXByBar(int bar, bool isStartOfBar = true) =>
        (int)Math.Round(isStartOfBar ? Chart.LeftX(bar) + Chart.BarSpacing / 2f : Chart.CenterX(bar));

    public int GetYByPrice(decimal price, bool isStartOfPrice = true)
    {
        var main = Chart.Main;
        // los precios de nivel (múltiplos del paso de fila) se ubican en el centro visual de su fila
        float y = RowCenterOf != null && price % main.RowSize == 0 ? main.Y(RowCenterOf(price)) : main.Y(price);
        if (isStartOfPrice) y -= (float)(main.RowHeight / 2);
        return (int)Math.Round(y);
    }

    /// <summary>Convierte un precio de nivel al centro de su fila (lo asigna el renderer).</summary>
    public Func<decimal, decimal>? RowCenterOf { get; set; }

    public decimal GetPriceByY(int y) => (decimal)Chart.Main.ValueAt(y);

    public int GetBarByX(int x) => Chart.BarAt(x);

    public int GetYByValue(decimal value) => (int)Math.Round(Y(value));

    public decimal GetValueByY(int y) => (decimal)ValueAt(y);
}
