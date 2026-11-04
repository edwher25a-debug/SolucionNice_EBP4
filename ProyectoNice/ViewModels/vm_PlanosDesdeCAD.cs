using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Autodesk.Revit.UI;
using ProyectoNice.Models;
using ProyectoNice.Utils;

namespace ProyectoNice.ViewModels
{
    /// <summary>
    ///     Monta planos de AutoCAD en Revit a partir de los manifiestos que escribe TrocearPlanos.
    ///
    ///     Por cada manifiesto: crea la hoja, una vista de dibujo por viewport, pasa la geometria
    ///     a lineas de detalle nativas con los estilos de la tabla de capas, crea los textos con
    ///     el tipo que corresponda a su altura en papel, pone el recorte igual a la ventana del
    ///     viewport y coloca la vista en la hoja en la misma posicion que tenia en el papel.
    ///
    ///     Una transaccion por plano: si uno falla se deshace completo y el lote sigue con el
    ///     siguiente. Y el modo ensayo no abre ninguna transaccion.
    /// </summary>
    public sealed class vm_PlanosDesdeCAD : vm_Herramienta
    {
        private const string Todos = "(todos)";
        private const string PorDefecto = "(por defecto)";

        //Datos de entrada
        public Document doc;

        //Carpeta
        private string carpetaTB = "";
        public string CarpetaTB
        {
            get => carpetaTB;
            set => SetProperty(ref carpetaTB, value);
        }

        //Planos encontrados
        private List<string> listaPlanosCB = new List<string> { Todos };
        public List<string> ListaPlanosCB
        {
            get => listaPlanosCB;
            private set => SetProperty(ref listaPlanosCB, value);
        }

        private string planoSeleccCB = Todos;
        public string PlanoSeleccCB
        {
            get => planoSeleccCB;
            set => SetProperty(ref planoSeleccCB, value);
        }

        //Plantilla del proyecto
        public List<FamilySymbol> ListaRotulosCB { get; }
        public FamilySymbol RotuloSeleccCB { get; set; }

        public List<ViewFamilyType> ListaTiposVistaCB { get; }
        public ViewFamilyType TipoVistaSeleccCB { get; set; }

        public List<string> ListaTiposVentanaCB { get; }
        public string TipoVentanaSeleccCB { get; set; }

        public List<string> ListaEstilosCB { get; }
        public string EstiloSinMapearSeleccCB { get; set; }

        //Calibracion papel de AutoCAD -> hoja de Revit. Se mide una vez y vale para toda la serie.
        public double DesfaseXTB { get; set; } = 0;
        public double DesfaseYTB { get; set; } = 0;

        //Tabla capa de CAD -> estilo de linea
        public ObservableCollection<m_MapeoCapa> Capas { get; } = new ObservableCollection<m_MapeoCapa>();

        public bool EnsayoCK { get; set; } = true;

        //Botones
        public RelayCommand ExaminarBT { get; }
        public RelayCommand LeerBT { get; }
        public RelayCommand AceptarBT { get; }

        private readonly List<GraphicsStyle> estilos;
        private readonly List<TextNoteType> tiposTexto;
        private readonly List<ElementType> tiposVentana;
        private List<m_PlanoCAD> planos = new List<m_PlanoCAD>();

        //Constructor
        public vm_PlanosDesdeCAD(Document doc)
        {
            this.doc = doc;

            ListaRotulosCB = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsElementType().Cast<FamilySymbol>()
                .OrderBy(f => f.FamilyName).ThenBy(f => f.Name).ToList();
            RotuloSeleccCB = ListaRotulosCB.FirstOrDefault();

            ListaTiposVistaCB = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(t => t.ViewFamily == ViewFamily.Drafting).OrderBy(t => t.Name).ToList();
            TipoVistaSeleccCB = ListaTiposVistaCB.FirstOrDefault();

            tiposVentana = new FilteredElementCollector(doc).OfClass(typeof(ElementType)).Cast<ElementType>()
                .Where(t => t.FamilyName == "Viewport").OrderBy(t => t.Name).ToList();
            ListaTiposVentanaCB = new List<string> { PorDefecto };
            ListaTiposVentanaCB.AddRange(tiposVentana.Select(t => t.Name));
            //Lo normal en un plano montado desde CAD es que la ventana no muestre titulo
            TipoVentanaSeleccCB = ListaTiposVentanaCB.FirstOrDefault(n => n.IndexOf("sin", StringComparison.OrdinalIgnoreCase) >= 0
                                                                      || n.IndexOf("no title", StringComparison.OrdinalIgnoreCase) >= 0) ?? PorDefecto;

            estilos = MetodoDesarrollo.EstilosDeLinea(doc);
            ListaEstilosCB = estilos.Select(e => e.Name).ToList();
            EstiloSinMapearSeleccCB = ListaEstilosCB.FirstOrDefault(n => n.IndexOf("revisar", StringComparison.OrdinalIgnoreCase) >= 0)
                                      ?? ListaEstilosCB.FirstOrDefault();

            tiposTexto = new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)).Cast<TextNoteType>().ToList();

            ExaminarBT = new RelayCommand(Examinar);
            LeerBT = new RelayCommand(Leer);
            AceptarBT = new RelayCommand(Validar);
        }

        //=== Lectura de la carpeta: no toca Revit, se puede correr con la ventana abierta ===

        private void Examinar()
        {
            var dialogo = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Elija cualquier manifiesto de la carpeta",
                Filter = "Manifiestos (*.json)|*.json|Todos|*.*",
                CheckFileExists = true
            };
            if (dialogo.ShowDialog() != true) return;

            CarpetaTB = Path.GetDirectoryName(dialogo.FileName) ?? "";
            Leer();
        }

        private void Leer()
        {
            planos = new List<m_PlanoCAD>();
            Capas.Clear();

            if (!Directory.Exists(CarpetaTB))
            {
                Aviso("Planos desde CAD: esa carpeta no existe.");
                ListaPlanosCB = new List<string> { Todos };
                PlanoSeleccCB = Todos;
                return;
            }

            var fallos = new List<string>();
            var codigos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var porCapa = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string ruta in Directory.GetFiles(CarpetaTB, "*.json").OrderBy(r => r))
            {
                try
                {
                    m_PlanoCAD plano = LectorJson.LeerPlano(ruta);

                    //Codigos duplicados: se para, nunca se inventa un sufijo
                    if (codigos.TryGetValue(plano.Codigo, out string anterior))
                    {
                        fallos.Add($"{Path.GetFileName(ruta)}: el codigo {plano.Codigo} ya lo trae {anterior}");
                        continue;
                    }
                    codigos[plano.Codigo] = Path.GetFileName(ruta);
                    planos.Add(plano);
                }
                catch (Exception ex)
                {
                    fallos.Add($"{Path.GetFileName(ruta)}: {ex.Message}");
                }
            }

            //Las capas de la geometria las declara el manifiesto: en Revit no se sabrian hasta
            //vincular cada dwg, y la tabla hay que poder llenarla antes de empezar.
            //Las de los textos se suman por si alguna solo aparece ahi.
            foreach (m_PlanoCAD plano in planos)
            {
                foreach (KeyValuePair<string, int> par in plano.Capas)
                    porCapa[par.Key] = porCapa.TryGetValue(par.Key, out int previas) ? previas + par.Value : par.Value;

                foreach (m_VistaCAD vista in plano.Vistas)
                    foreach (m_TextoCAD texto in vista.Textos)
                    {
                        if (texto.Capa == "") continue;
                        porCapa[texto.Capa] = porCapa.TryGetValue(texto.Capa, out int n) ? n + 1 : 1;
                    }
            }

            foreach (KeyValuePair<string, int> par in porCapa.OrderBy(p => p.Key))
                Capas.Add(new m_MapeoCapa
                {
                    Capa = par.Key,
                    Planos = par.Value,
                    EstiloSelecc = ListaEstilosCB.FirstOrDefault(n => string.Equals(n, par.Key, StringComparison.OrdinalIgnoreCase)) ?? ""
                });

            var nombres = new List<string> { Todos };
            nombres.AddRange(planos.Select(p => p.ToString()));
            ListaPlanosCB = nombres;
            PlanoSeleccCB = Todos;

            int vistas = planos.Sum(p => p.Vistas.Count);
            string resumen = $"Planos desde CAD: {planos.Count} manifiesto(s), {vistas} vista(s).";
            if (fallos.Count > 0) resumen += $" {fallos.Count} descartado(s): {string.Join(" | ", fallos)}";
            Aviso(resumen);
        }

        //=== Proceso ===

        public void Validar()
        {
            if (planos.Count == 0) { Aviso("Planos desde CAD: primero lea una carpeta con manifiestos."); return; }
            if (RotuloSeleccCB == null) { Aviso("Planos desde CAD: el proyecto no tiene ningun cuadro de rotulacion cargado."); return; }
            if (TipoVistaSeleccCB == null) { Aviso("Planos desde CAD: el proyecto no tiene ningun tipo de vista de dibujo."); return; }
            if (tiposTexto.Count == 0) { Aviso("Planos desde CAD: el proyecto no tiene ningun tipo de texto."); return; }

            Ejecutar(Aceptar);
        }

        public void Aceptar()
        {
            List<m_PlanoCAD> aProcesar = PlanoSeleccCB == Todos
                ? planos
                : planos.Where(p => p.ToString() == PlanoSeleccCB).ToList();

            var bitacora = new StringBuilder();
            bitacora.AppendLine($"Planos desde CAD - {DateTime.Now:yyyy-MM-dd HH:mm}");
            bitacora.AppendLine(EnsayoCK ? "MODO ENSAYO: no se escribio nada en el proyecto." : "Proceso real.");
            bitacora.AppendLine($"Carpeta: {CarpetaTB}");
            bitacora.AppendLine($"Desfase papel -> hoja: {DesfaseXTB} ; {DesfaseYTB} mm");
            bitacora.AppendLine();

            int planosOk = 0, planosFallidos = 0;
            var noCuadran = new List<string>();

            foreach (m_PlanoCAD plano in aProcesar)
            {
                bitacora.AppendLine($"=== {plano.Codigo} - {plano.Titulo} ({plano.Layout}) ===");
                foreach (string aviso in plano.Avisos) bitacora.AppendLine($"  aviso de AutoCAD: {aviso}");

                if (EnsayoCK)
                {
                    foreach (string linea in Ensayar(plano)) bitacora.AppendLine("  " + linea);
                    bitacora.AppendLine();
                    planosOk++;
                    continue;
                }

                using (var transaccion = new Transaction(doc, $"Montar plano {plano.Codigo}"))
                {
                    transaccion.Start();
                    try
                    {
                        List<m_ReporteVista> reportes = MontarPlano(plano, bitacora);
                        transaccion.Commit();
                        planosOk++;
                        noCuadran.AddRange(reportes.Where(r => !r.Cuadra).Select(r => $"{plano.Codigo}/{r.Vista}"));
                    }
                    catch (Exception ex)
                    {
                        if (!transaccion.HasEnded()) transaccion.RollBack();
                        planosFallidos++;
                        bitacora.AppendLine($"  FALLO, el plano se deshizo completo: {ex.Message}");
                    }
                }

                bitacora.AppendLine();
            }

            //=== Reporte ===
            string rutaBitacora = "";
            try
            {
                rutaBitacora = Path.Combine(CarpetaTB, "_reporte_PlanosDesdeCAD.txt");
                File.WriteAllText(rutaBitacora, bitacora.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                rutaBitacora = $"(no se pudo escribir el reporte: {ex.Message})";
            }

            string resumen = EnsayoCK
                ? $"Planos desde CAD (ensayo): {planosOk} plano(s) revisado(s). Nada escrito."
                : $"Planos desde CAD: {planosOk} plano(s) montado(s), {planosFallidos} fallido(s).";
            if (noCuadran.Count > 0) resumen += $" {noCuadran.Count} vista(s) con cuentas que no cuadran: {string.Join(", ", noCuadran.Take(6))}.";
            resumen += $" Reporte: {rutaBitacora}";

            Aviso(resumen);
            TaskDialog.Show("Planos desde CAD", resumen);
        }

        //=== Ensayo: todo lo que se puede comprobar sin escribir ===

        private List<string> Ensayar(m_PlanoCAD plano)
        {
            var lineas = new List<string>();

            ViewSheet existente = BuscarPlano(plano.Codigo);
            lineas.Add(existente == null
                ? $"hoja {plano.Codigo}: se creara"
                : $"hoja {plano.Codigo}: ya existe, se reutilizara y sus vistas se reconstruiran");

            //Tamano de papel contra el cuadro de rotulacion elegido: si no coinciden las vistas
            //caen fuera de la hoja y nada falla
            double anchoRotulo = AnchoRotuloMm(out double altoRotulo);
            if (anchoRotulo > 0 && plano.PapelAnchoMm > 0)
            {
                bool igual = Math.Abs(anchoRotulo - plano.PapelAnchoMm) < 5 && Math.Abs(altoRotulo - plano.PapelAltoMm) < 5;
                lineas.Add($"papel: dwg {plano.PapelAnchoMm:0}x{plano.PapelAltoMm:0} mm, rotulo {anchoRotulo:0}x{altoRotulo:0} mm" + (igual ? "" : "  <- NO COINCIDEN"));
            }

            foreach (m_VistaCAD vista in plano.Vistas)
            {
                var notas = new List<string>();

                string rutaDwg = Path.Combine(plano.Carpeta, vista.Dwg);
                if (!File.Exists(rutaDwg)) notas.Add("FALTA el dwg");

                if (vista.EscalaDenominador <= 0) notas.Add("escala invalida");
                if (vista.VentanaAnchoM <= 0 || vista.VentanaAltoM <= 0) notas.Add("ventana sin tamano");

                ViewDrafting vistaExistente = BuscarVista(vista.Nombre);
                if (vistaExistente != null) notas.Add("la vista ya existe");

                //Alturas de texto que no tienen un tipo cercano en el proyecto
                var sinTipo = vista.Textos.Select(t => t.AlturaMm).Distinct()
                    .Where(a => ConversorCAD.TipoMasCercano(tiposTexto, a) == null)
                    .Select(a => $"{a:0.##}")
                    .ToList();
                if (sinTipo.Count > 0) notas.Add($"alturas sin tipo de texto: {string.Join(", ", sinTipo)} mm");

                string papel = $"papel ({vista.PapelCentroXMm + DesfaseXTB:0.0} ; {vista.PapelCentroYMm + DesfaseYTB:0.0})";
                lineas.Add($"{vista.Nombre}: 1:{vista.EscalaDenominador}, ventana {vista.VentanaAnchoM:0.00}x{vista.VentanaAltoM:0.00} m, "
                           + $"{vista.Textos.Count} texto(s), {papel}" + (notas.Count > 0 ? "  <- " + string.Join("; ", notas) : ""));
            }

            //Tipos de texto disponibles, para que se vea con que se va a mapear
            lineas.Add("tipos de texto del proyecto: " + string.Join(", ",
                tiposTexto.Select(t => $"{t.Name} ({ConversorCAD.AlturaMm(t):0.##} mm)").OrderBy(s => s)));

            return lineas;
        }

        //=== Montaje real ===

        private List<m_ReporteVista> MontarPlano(m_PlanoCAD plano, StringBuilder bitacora)
        {
            var reportes = new List<m_ReporteVista>();

            //01_Hoja
            ViewSheet hoja = BuscarPlano(plano.Codigo);
            if (hoja == null)
            {
                hoja = ViewSheet.Create(doc, RotuloSeleccCB.Id);
                try { hoja.SheetNumber = plano.Codigo; }
                catch (Exception ex) { throw new InvalidOperationException($"no se pudo poner el numero de hoja {plano.Codigo}: {ex.Message}"); }
            }
            try { hoja.Name = plano.Titulo; } catch { bitacora.AppendLine($"  no se pudo poner el nombre de hoja \"{plano.Titulo}\""); }
            doc.Regenerate();

            //02_Mapeo capa -> estilo, resuelto una vez por plano
            Dictionary<string, GraphicsStyle> mapeo = Mapeo();
            GraphicsStyle sinMapear = estilos.FirstOrDefault(e => e.Name == EstiloSinMapearSeleccCB);
            ElementType tipoVentana = tiposVentana.FirstOrDefault(t => t.Name == TipoVentanaSeleccCB);

            //03_Una subtransaccion por vista: la que falle no se lleva el plano entero
            foreach (m_VistaCAD datos in plano.Vistas)
            {
                var reporte = new m_ReporteVista { Vista = datos.Nombre };

                using (var paso = new SubTransaction(doc))
                {
                    paso.Start();
                    try
                    {
                        ViewDrafting vista = BuscarVista(datos.Nombre);
                        if (vista == null)
                        {
                            vista = ViewDrafting.Create(doc, TipoVistaSeleccCB.Id);
                            try { vista.Name = datos.Nombre; }
                            catch (Exception ex) { throw new InvalidOperationException($"nombre de vista rechazado: {ex.Message}"); }
                        }

                        if (datos.EscalaDenominador > 0) vista.Scale = datos.EscalaDenominador;

                        //La identidad real es archivo + handle del viewport, no el nombre:
                        //el Number de un viewport se reasigna si alguien añade o borra viewports.
                        Parameter descripcion = vista.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION);
                        if (descripcion != null && !descripcion.IsReadOnly) descripcion.Set(datos.Identidad(plano.ArchivoOrigen));

                        reporte = ConversorCAD.Rellenar(doc, vista, datos, Path.Combine(plano.Carpeta, datos.Dwg),
                            mapeo, sinMapear, tiposTexto);

                        ConversorCAD.AjustarRecorte(vista, datos);
                        doc.Regenerate();

                        string colocacion = Colocar(hoja, vista, datos, tipoVentana);
                        if (colocacion != "") bitacora.AppendLine($"  {datos.Nombre}: {colocacion}");

                        paso.Commit();
                    }
                    catch (Exception ex)
                    {
                        if (!paso.HasEnded()) paso.RollBack();
                        reporte.Error = ex.Message;
                    }
                }

                reportes.Add(reporte);
                bitacora.AppendLine("  " + reporte.Resumen());
            }

            return reportes;
        }

        private string Colocar(ViewSheet hoja, ViewDrafting vista, m_VistaCAD datos, ElementType tipoVentana)
        {
            //El centro de la vista en la hoja es el centro del viewport en el papel: la vista esta
            //recentrada en su ventana y el recorte es simetrico, asi que es una copia directa.
            var punto = new XYZ(ConversorCAD.Mm(datos.PapelCentroXMm + DesfaseXTB),
                                ConversorCAD.Mm(datos.PapelCentroYMm + DesfaseYTB), 0);

            Viewport ventana = hoja.GetAllViewports().Select(id => doc.GetElement(id) as Viewport)
                .FirstOrDefault(v => v != null && v.ViewId == vista.Id);

            if (ventana == null)
            {
                if (!Viewport.CanAddViewToSheet(doc, hoja.Id, vista.Id))
                    return "la vista ya esta colocada en otro plano, no se coloco aqui";
                ventana = Viewport.Create(doc, hoja.Id, vista.Id, punto);
            }

            if (tipoVentana != null && ventana.GetTypeId() != tipoVentana.Id)
            {
                try { ventana.ChangeTypeId(tipoVentana.Id); } catch { /* el tipo no aplica */ }
            }

            doc.Regenerate();
            ventana.SetBoxCenter(punto);
            return "";
        }

        //=== Apoyos ===

        private Dictionary<string, GraphicsStyle> Mapeo()
        {
            var mapeo = new Dictionary<string, GraphicsStyle>(StringComparer.OrdinalIgnoreCase);
            foreach (m_MapeoCapa fila in Capas)
            {
                if (fila.Capa == "" || fila.EstiloSelecc == "") continue;
                GraphicsStyle estilo = estilos.FirstOrDefault(e => e.Name == fila.EstiloSelecc);
                if (estilo != null) mapeo[fila.Capa] = estilo;
            }
            return mapeo;
        }

        private ViewSheet BuscarPlano(string numero) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .FirstOrDefault(s => !s.IsPlaceholder && s.SheetNumber == numero);

        private ViewDrafting BuscarVista(string nombre) =>
            new FilteredElementCollector(doc).OfClass(typeof(ViewDrafting)).Cast<ViewDrafting>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == nombre);

        //Tamano del cuadro de rotulacion elegido, en mm
        private double AnchoRotuloMm(out double altoMm)
        {
            altoMm = 0;
            if (RotuloSeleccCB == null) return 0;

            Parameter ancho = RotuloSeleccCB.get_Parameter(BuiltInParameter.SHEET_WIDTH);
            Parameter alto = RotuloSeleccCB.get_Parameter(BuiltInParameter.SHEET_HEIGHT);
            if (ancho == null || alto == null) return 0;

            altoMm = ConversorCAD.AMm(alto.AsDouble());
            return ConversorCAD.AMm(ancho.AsDouble());
        }
    }
}
