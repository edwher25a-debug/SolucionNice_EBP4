using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ProyectoNice.Acotado;
using ProyectoNice.Utils;

namespace ProyectoNice.ViewModels
{
    /// <summary>
    ///     Acota vistas segun lo que muestran: planta de pila o estribo, planta de viga cabezal,
    ///     seccion de columna, alzado o seccion, y seccion de box. Ver Acotado/Reglas.cs.
    /// </summary>
    public sealed class vm_AcotarVistas : vm_Herramienta
    {
        private const string VistaActiva = "Vista activa";
        private const string Seleccionadas = "Vistas seleccionadas en el navegador";
        private const string DeUnPlano = "Todas las vistas de un plano";
        private const string Ninguno = "(por defecto de Revit)";

        //Datos de entrada
        public Document doc;
        public Selection seleccion;

        //Que vistas
        public List<string> ListaAlcancesCB { get; } = new List<string> { VistaActiva, Seleccionadas, DeUnPlano };
        public string AlcanceSeleccCB { get; set; } = VistaActiva;
        public List<string> ListaPlanosCB { get; }
        public string? PlanoSeleccCB { get; set; }
        public bool OmitirAcotadasCK { get; set; } = true;

        //Como
        public List<TipoVista> ListaTiposVistaCB { get; } = Enum.GetValues(typeof(TipoVista)).Cast<TipoVista>().Where(t => t != TipoVista.Ninguno).ToList();
        public TipoVista TipoVistaSeleccCB { get; set; } = TipoVista.Automatico;
        public List<string> ListaTiposCotaCB { get; }
        public string TipoCotaSeleccCB { get; set; }
        public List<string> ListaTiposNivelCB { get; }
        public string TipoNivelSeleccCB { get; set; }
        public List<string> ListaEstilosCB { get; }
        public string EstiloMarcasSeleccCB { get; set; }
        public double PrimeraFilaTB { get; set; } = 8;
        public double EntreFilasTB { get; set; } = 7;
        public double PiloteVisibleTB { get; set; } = 1;
        public bool NivelesCK { get; set; } = true;
        public bool EtiquetasCK { get; set; } = true;
        public bool DiametrosCK { get; set; } = true;

        //Botones
        public RelayCommand AceptarBT { get; set; }

        private readonly List<ViewSheet> planos;
        private readonly List<DimensionType> tiposCota;
        private readonly List<SpotDimensionType> tiposNivel;
        private readonly List<GraphicsStyle> estilos;

        //Constructor
        public vm_AcotarVistas(Document doc, Selection seleccion)
        {
            this.doc = doc;
            this.seleccion = seleccion;

            planos = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder).OrderBy(s => s.SheetNumber).ToList();
            ListaPlanosCB = planos.Select(NombrePlano).ToList();
            PlanoSeleccCB = ListaPlanosCB.FirstOrDefault();

            OpcionesAcotado porDefecto = OpcionesAcotado.PorDefecto(doc);
            tiposCota = OpcionesAcotado.TiposCota(doc);
            tiposNivel = OpcionesAcotado.TiposNivel(doc);
            estilos = MetodoDesarrollo.EstilosDeLinea(doc);

            ListaTiposCotaCB = ConNinguno(tiposCota.Select(t => t.Name));
            TipoCotaSeleccCB = porDefecto.TipoCota?.Name ?? Ninguno;
            ListaTiposNivelCB = ConNinguno(tiposNivel.Select(t => t.Name));
            TipoNivelSeleccCB = porDefecto.TipoNivel?.Name ?? Ninguno;
            ListaEstilosCB = ConNinguno(estilos.Select(e => e.Name));
            EstiloMarcasSeleccCB = porDefecto.EstiloMarcas?.Name ?? Ninguno;

            AceptarBT = new RelayCommand(Validar);
        }

        private static string NombrePlano(ViewSheet plano) => $"{plano.SheetNumber} - {plano.Name}";

        private static List<string> ConNinguno(IEnumerable<string> nombres) => new[] { Ninguno }.Concat(nombres).ToList();

        public void Validar()
        {
            if (PrimeraFilaTB <= 0 || EntreFilasTB <= 0 || PiloteVisibleTB < 0)
            {
                Aviso("Acotar vistas: las separaciones deben ser mayores que cero y los metros de pilote no pueden ser negativos.");
                return;
            }

            Ejecutar(Aceptar);
        }

        public void Aceptar()
        {
            List<View> vistas = VistasElegidas();
            if (vistas.Count == 0)
            {
                Aviso("Acotar vistas: no hay plantas, secciones ni alzados para acotar con lo elegido.");
                return;
            }

            var opciones = new OpcionesAcotado
            {
                TipoCota = tiposCota.FirstOrDefault(t => t.Name == TipoCotaSeleccCB),
                TipoNivel = tiposNivel.FirstOrDefault(t => t.Name == TipoNivelSeleccCB),
                EstiloMarcas = estilos.FirstOrDefault(e => e.Name == EstiloMarcasSeleccCB),
                Forzar = TipoVistaSeleccCB,
                PrimeraFila = PrimeraFilaTB,
                EntreFilas = EntreFilasTB,
                PiloteVisible = PiloteVisibleTB,
                Niveles = NivelesCK,
                Etiquetas = EtiquetasCK,
                Diametros = DiametrosCK
            };

            List<ResultadoAcotado> resultados;
            using (var transaccion = new Transaction(doc, "Acotar vistas"))
            {
                transaccion.Start();
                resultados = vistas.Select(v => Acotador.Acotar(v, opciones)).ToList();
                transaccion.Commit();
            }

            int cotas = resultados.Sum(r => r.Cotas);
            Aviso($"Acotar vistas: {vistas.Count} vista(s), {cotas} cotas, {resultados.Sum(r => r.Niveles)} niveles.");
            TaskDialog.Show("Acotar vistas", string.Join("\n\n", resultados.Select(r => r.Resumen)));
        }

        private List<View> VistasElegidas()
        {
            IEnumerable<View> vistas;
            if (AlcanceSeleccCB == Seleccionadas)
                vistas = seleccion.GetElementIds().Select(doc.GetElement)
                    .Select(e => e is Viewport ventana ? doc.GetElement(ventana.ViewId) : e)
                    .OfType<View>();
            else if (AlcanceSeleccCB == DeUnPlano)
                vistas = planos.FirstOrDefault(p => NombrePlano(p) == PlanoSeleccCB)?.GetAllPlacedViews().Select(doc.GetElement).OfType<View>()
                         ?? Enumerable.Empty<View>();
            else
                vistas = new[] { doc.ActiveView };

            return vistas
                .Where(v => (v is ViewPlan || v is ViewSection) && !v.IsTemplate)
                .GroupBy(v => v.Id).Select(g => g.First())
                .Where(v => !OmitirAcotadasCK || !new FilteredElementCollector(doc, v.Id).OfClass(typeof(Dimension)).Any())
                .ToList();
        }
    }
}
