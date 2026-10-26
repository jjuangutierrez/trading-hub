[← Volver al índice](README.md)

# Indicadores

Los indicadores del gráfico se agregan desde la ventana **Indicadores** (botón **Indicadores** de la barra del gráfico,
o **Gráfico → Indicadores…**, ⌘I). A la izquierda está la **Biblioteca**, con un buscador por nombre o categoría;
**Agregar al gráfico** (o doble clic) lo suma, y al elegir uno de los que ya están en el gráfico se editan sus
propiedades a la derecha. **Plantillas** guarda los ajustes de un indicador, aplica uno guardado o elige con cuál
arrancan los nuevos. El resto del manejo del gráfico está en [Gráficos](graficos.md#indicadores).

Los indicadores de las estrategias (`Sma`, `Ema`, `Rsi`…) son otra API, más simple: ver [Estrategias](estrategias.md).

## Incluidos

| Indicador | Categoría | Opciones principales |
|---|---|---|
| Media móvil | Tendencia | Período, tipo (simple, exponencial o ponderada) y fuente |
| Bandas de Bollinger | Tendencia | Período y cantidad de desvíos |
| VWAP con desvíos | Volumen / Order flow | Se reinicia cada sesión, semana o mes; hasta tres bandas de desvío ponderado por volumen |
| Volumen | Volumen / Order flow | Colores; se colorea por delta o por vela |
| Delta | Volumen / Order flow | Histograma o velas con el máximo y el mínimo del delta |
| Delta acumulado (CVD) | Volumen / Order flow | En velas; se reinicia o no en cada sesión |
| Big Trades (grandes operaciones) | Volumen / Order flow | Burbujas en las órdenes agresivas grandes (suma las ejecuciones de una misma orden aunque barra varios niveles). Umbral fijo o adaptativo (percentil de las órdenes recientes), dónde se ubica en el precio, colores y tamaños |
| Perfil de volumen por sesión | Volumen / Order flow | Por sesión, semana o toda la historia visible; volumen, delta o bid/ask; área de valor (%), POC, VAH y VAL |
| Flow VWAP Anclado | Volumen / Order flow | VWAP anclado a un swing, un pico de volumen o una fecha manual; bandas de desvío y burbujas de flujo |
| Cluster Search: Refill Zones | Volumen / Order flow | Clústeres de absorción pasiva (refill) proyectados hasta que el precio los toca; filtros de volumen y absorción, colores y zonas mitigadas |
| Multi Volume Profile por Sesión | Volumen / Order flow | Varios perfiles por día, semana o mes; composición AMT, área de valor y POC proyectado hasta que el precio lo toca |
| RSI | Osciladores | Período |
| MACD | Osciladores | Medias rápida y lenta, y señal |

Vienen en el proyecto `TradingHub.Indicators.Native` y en la biblioteca aparecen agrupados por categoría.

### Indicadores técnicos

El proyecto `TradingHub.Indicators.Technical` está pensado para la colección de indicadores técnicos de ATAS (unos
250). Su código es público pero no tiene una licencia abierta, así que **no se publica** en este repositorio: el proyecto
solo trae lo mínimo para compilarlos (los tipos de editores que usan). Los sumás vos en un minuto, como se explica en
[Sumar los indicadores de ATAS](#sumar-los-indicadores-de-atas), y Trading Hub los carga solo: aparecen en la
biblioteca con los demás, por categoría.

## Indicadores propios

Escribís los tuyos en C# y Trading Hub los compila solo.

- Van en `~/Documents/Trading Hub/Indicadores` (se pueden usar subcarpetas; se ignoran `bin` y `obj`). Con la carpeta
  vacía, Trading Hub deja un ejemplo: `EjemploDeltaPorcentual.cs` («Ejemplo: delta % por vela»).
- Trading Hub vigila la carpeta: cuando guardás, creás, borrás o renombrás un archivo, recompila todo apenas pasan 0,6
  segundos sin cambios. También podés forzarlo con **Código → Recompilar indicadores**.
- Todos los archivos se compilan juntos. Si hay un error, siguen disponibles los de la última compilación buena y la
  barra de estado muestra el primer error; si compila, avisa «Indicadores propios compilados en N ms».
- Aparecen arriba de todo en la biblioteca, en el grupo «Mis indicadores» (con una estrella) y con la etiqueta
  «propio». Si cambiaste el código de uno que ya está en un gráfico, quitalo y volvelo a agregar para usar la versión
  nueva.

### El editor

**Código → Editor de indicadores…** (⇧⌘E), o el botón de código de la ventana Indicadores, abre un editor con la lista
de archivos a la izquierda. **Nuevo** crea un indicador desde una plantilla (te pide el nombre de la clase) y **Guardar y
compilar** (⌘S o ⌘B) lo guarda y recompila; los errores se listan abajo y tocando uno vas a la línea. El botón de la
carpeta la abre en el Finder.

### Ejemplo

```csharp
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using TradingHub.Indicators;

[DisplayName("Rango promedio")]
[Category("Mis indicadores")]
public class RangoPromedio : Indicator
{
    private readonly ValueDataSeries _serie = new("Rango", "Rango promedio") { Color = Color.Orange, Width = 2 };
    private int _periodo = 14;

    [Display(Name = "Período", GroupName = "Configuración")]
    [Range(1, 1000)]
    public int Periodo
    {
        get => _periodo;
        set { _periodo = value; RecalculateValues(); }
    }

    public RangoPromedio()
    {
        Panel = IndicatorDataProvider.NewPanel; // en un panel propio, debajo del precio
        DataSeries[0] = _serie;
    }

    protected override void OnCalculate(int bar, decimal value)
    {
        if (bar < Periodo - 1) return; // al principio no hay velas suficientes
        decimal suma = 0;
        for (int i = 0; i < Periodo; i++)
        {
            var vela = GetCandle(bar - i);
            suma += vela.High - vela.Low;
        }

        _serie[bar] = suma / Periodo;
    }
}
```

### La API, en corto

- Una clase pública que hereda de `Indicator`, con constructor sin parámetros, `[DisplayName("…")]` y `[Category("…")]`.
  `System`, `System.Linq` y `System.Collections.Generic` ya están; sumá `System.ComponentModel`,
  `System.ComponentModel.DataAnnotations`, `System.Drawing` y `TradingHub.Indicators` (y `TradingHub.Indicators.Drawing`,
  `TradingHub.Rendering.Context` o `TradingHub.Rendering.Tools` si dibujás vos).
- **Series**: `ValueDataSeries` (línea o histograma con `VisualType = VisualMode.Histogram`), `RangeDataSeries`
  (bandas, con `new RangeValue { Upper = a, Lower = b }`) y `PaintbarsDataSeries` (pinta las velas). La principal va en
  `DataSeries[0]` y las demás con `DataSeries.Add(…)`.
- **Panel**: por defecto sobre el precio; `Panel = IndicatorDataProvider.NewPanel` lo pone en uno propio.
- **Parámetros**: propiedades públicas con `[Display(Name = "…", GroupName = "…")]` y, si querés, `[Range(mín, máx)]`;
  en el `set`, guardá el valor y llamá a `RecalculateValues()`.
- **Cálculo**: `OnCalculate(int bar, decimal value)` se llama para cada vela (`bar` desde 0, `value` el cierre).
  `GetCandle(bar)` da `Open`, `High`, `Low`, `Close`, `Volume`, `Ask`, `Bid`, `Delta`, `MaxDelta`, `MinDelta` y `Time`;
  `CurrentBar` es la cantidad de velas. Cuidá los índices negativos al principio.
- **Dibujo propio** (opcional): en el constructor, `EnableCustomDrawing = true;` y
  `SubscribeToDrawingEvents(DrawingLayouts.Final);`, y sobrescribí `OnRender(RenderContext context, DrawingLayouts layout)`
  usando `ChartInfo.GetXByBar(bar)`, `ChartInfo.GetYByPrice(precio)` y los métodos de `context` (`DrawLine`,
  `FillRectangle`, `DrawString`…). Las velas visibles van de `FirstVisibleBarNumber` a `LastVisibleBarNumber`.

Claude también escribe indicadores: pedíselo en el panel de Backtesting y los deja en esta carpeta (ver
[Claude](asistente-claude.md)).

## DLLs extra

Si tenés indicadores ya compilados en una DLL (clases que heredan de `TradingHub.Indicators.Indicator`), copiala en
`~/Library/Application Support/Trading Hub/Indicators` o en una carpeta `Indicators` al lado del ejecutable de la app.
Se cargan al abrir Trading Hub y aparecen en su categoría (la de su `[Category]`, u «Otros») con la etiqueta «extra».
Una que no se puede cargar deja un aviso y no frena al resto.

## Sumar los indicadores de ATAS

ATAS publica el código de sus indicadores en [AtasPlatform/Indicators](https://github.com/AtasPlatform/Indicators).
Hay dos formas de usarlos en Trading Hub.

**Todos de una vez**, dentro de la app (desde la carpeta del repositorio):

```bash
git clone https://github.com/AtasPlatform/Indicators.git third_party/ATAS-Indicators
python3 tools/port_atas.py
./build-app.sh --install
```

`tools/port_atas.py` copia los indicadores de la carpeta `Technical` a `src/TradingHub.Indicators.Technical/Atas`,
cambia los namespaces de ATAS por los de Trading Hub (la tabla de abajo) sin tocar la lógica y aplica unos pocos
arreglos para que compilen y no dividan por cero. Deja afuera lo que depende de partes internas de ATAS (sus editores
de ventanas, el heatmap y el DOM v3). Si querés excluir otros, pasalos separados por coma en
`TRADINGHUB_PORT_EXCLUDE=Archivo1.cs,Archivo2.cs`. Para actualizarlos cuando ATAS publique cambios:

```bash
git -C third_party/ATAS-Indicators pull
python3 tools/port_atas.py
./build-app.sh --install
```

`third_party/` y `src/TradingHub.Indicators.Technical/Atas/` están en el `.gitignore`: el código de ATAS queda en tu
copia y no se sube si hacés un fork.

**Uno por uno**, sin compilar la app: copiá el `.cs` en `~/Documents/Trading Hub/Indicadores` y se compila como un
indicador propio (con la misma adaptación de namespaces). Los que usan otros indicadores de ATAS necesitan que copies
también esos: por ejemplo, el `CCI.cs` y el `Momentum.cs` de ATAS usan su `SMA.cs`. Lo mismo vale para indicadores en C#
de cualquier otra fuente de código público escritos para la API de ATAS.

Esos indicadores son de sus autores: usalos respetando la licencia de cada fuente.

## Compatibilidad con la API de ATAS

La API de indicadores de Trading Hub tiene la misma forma que la de ATAS (`Indicator`, `OnCalculate`, `DataSeries`,
`OnRender`, `GetCandle`, `ChartInfo`…), así que podés pegar indicadores escritos para ATAS. Antes de compilar, Trading Hub
cambia solo los namespaces:

| En el código de ATAS | Se compila como |
|---|---|
| `ATAS.Indicators` · `ATAS.Indicators.Drawing` · `ATAS.Indicators.Technical` | `TradingHub.Indicators` · `TradingHub.Indicators.Drawing` · `TradingHub.Indicators.Technical` |
| `ATAS.DataFeedsCore` | `TradingHub.Trading` |
| `OFT.Rendering` y sus `Context`, `Context.GDIPlus`, `Settings`, `Tools`, `Control` | `TradingHub.Rendering` y los equivalentes |
| `OFT.Attributes` · `OFT.Attributes.Editors` · `OFT.Localization` | `TradingHub.Attributes` · `TradingHub.Attributes.Editors` · `TradingHub.Localization` |
| `Utils.Common` · `Utils.Common.Logging` · `Utils.Common.Collections` | `TradingHub.Common` y los equivalentes |

Además están definidos los alias `CrossColor` y `CrossColors` (= `System.Drawing.Color`), `CrossPen`, `CrossPens`,
`CrossSolidBrush`, `CrossKey` y `CrossKeyEventArgs`, y los símbolos de preprocesador `TRADINGHUB` y `CROSS_PLATFORM`.
Con `#if TRADINGHUB … #else … #endif` un mismo archivo puede tener lo que solo compila acá y lo que solo compila en ATAS.
La adaptación vale también para las estrategias. No todo lo de ATAS tiene equivalente: si algo no compila, el error
dice qué falta.
