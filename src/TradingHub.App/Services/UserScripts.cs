using Avalonia.Threading;
using TradingHub.Common.Logging;
using TradingHub.Scripting;

namespace TradingHub.App.Services;

/// <summary>
/// Indicadores propios del usuario: compila los .cs de ~/Documents/Trading Hub/Indicadores al arrancar y
/// cada vez que cambian (recarga en caliente), y los suma al catálogo.
/// </summary>
public sealed class UserScripts : IDisposable
{
    public const string Source = "Mis indicadores";
    private readonly IndicatorCatalog _catalog;
    private readonly ScriptCompiler _compiler = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly DispatcherTimer _debounce;

    public UserScripts(IndicatorCatalog catalog)
    {
        _catalog = catalog;
        Folder = AppPaths.UserIndicators;
        try
        {
            Directory.CreateDirectory(Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // carpeta de solo lectura (sandbox, permisos, Documents protegido): la app arranca igual
            this.LogWarn("No se pudo preparar la carpeta de indicadores propios: " + ex.Message);
        }

        WriteExampleIfEmpty();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            CompileAll();
        };

        try
        {
            _watcher = new FileSystemWatcher(Folder, "*.cs") { IncludeSubdirectories = true, EnableRaisingEvents = true };
            _watcher.Changed += (_, _) => Dispatcher.UIThread.Post(Restart);
            _watcher.Created += (_, _) => Dispatcher.UIThread.Post(Restart);
            _watcher.Deleted += (_, _) => Dispatcher.UIThread.Post(Restart);
            _watcher.Renamed += (_, _) => Dispatcher.UIThread.Post(Restart);
        }
        catch (Exception ex)
        {
            this.LogWarn("No se pudo vigilar la carpeta de indicadores: " + ex.Message);
        }
    }

    public string Folder { get; }
    public ScriptCompileResult? LastResult { get; private set; }

    /// <summary>Se dispara después de cada compilación (con éxito o con errores).</summary>
    public event Action<ScriptCompileResult>? Compiled;

    private void Restart()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    public ScriptCompileResult? CompileAll()
    {
        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(Folder, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/bin/") && !f.Contains("/obj/"))
                .OrderBy(f => f)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // la carpeta puede no existir o no ser legible: no es un error fatal al arrancar
            this.LogWarn("No se pudieron leer los indicadores propios: " + ex.Message);
            return LastResult;
        }

        if (files.Count == 0)
        {
            _catalog.RemoveSource(Source);
            return null;
        }

        ScriptCompileResult res;
        try
        {
            res = _compiler.Compile(files, "TradingHubUserIndicators");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // un archivo a medio guardar o que se movió: se reintenta con el próximo cambio de la carpeta
            this.LogWarn("No se pudieron leer los indicadores propios: " + ex.Message);
            Restart();
            return LastResult;
        }

        LastResult = res;
        if (res.Success && res.Assembly != null)
        {
            _catalog.RemoveSource(Source);
            _catalog.Scan(res.Assembly, Source);
            this.LogInfo($"Indicadores propios compilados ({files.Count} archivos, {res.Elapsed.TotalMilliseconds:0} ms)");
        }
        else
        {
            foreach (var d in res.Diagnostics.Where(d => d.Severity == "error").Take(20)) this.LogWarn(d.ToString());
        }

        Compiled?.Invoke(res);
        return res;
    }

    private void WriteExampleIfEmpty()
    {
        try
        {
            if (Directory.EnumerateFiles(Folder, "*.cs", SearchOption.AllDirectories).Any()) return;
            File.WriteAllText(Path.Combine(Folder, "EjemploDeltaPorcentual.cs"), ExampleSource);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // no poder escribir el ejemplo no debe impedir que Trading Hub abra
            this.LogWarn("No se pudo crear el indicador de ejemplo: " + ex.Message);
        }
    }

    public const string ExampleSource = """
        // Ejemplo de indicador propio. Guardá cambios y Trading Hub lo recompila solo.
        // La API es la misma que la de ATAS: podés pegar indicadores escritos para ATAS.
        using System.ComponentModel;
        using System.ComponentModel.DataAnnotations;
        using System.Drawing;
        using TradingHub.Indicators;

        [DisplayName("Ejemplo: delta % por vela")]
        [Category("Mis indicadores")]
        public class EjemploDeltaPorcentual : Indicator
        {
            private readonly ValueDataSeries _serie = new("DeltaPct", "Delta %")
            {
                VisualType = VisualMode.Histogram,
            };

            [Display(Name = "Umbral para resaltar (%)", GroupName = "Configuración")]
            public decimal Umbral { get; set; } = 30;

            public EjemploDeltaPorcentual()
            {
                Panel = IndicatorDataProvider.NewPanel;
                DataSeries[0] = _serie;
            }

            protected override void OnCalculate(int bar, decimal value)
            {
                var vela = GetCandle(bar);
                var pct = vela.Volume > 0 ? vela.Delta / vela.Volume * 100 : 0;
                _serie[bar] = pct;
                _serie.Colors[bar] = Math.Abs(pct) >= Umbral
                    ? (pct > 0 ? Color.FromArgb(255, 73, 209, 73) : Color.FromArgb(255, 232, 69, 72))
                    : Color.FromArgb(255, 110, 120, 140);
            }
        }
        """;

    public void Dispose() => _watcher?.Dispose();
}
