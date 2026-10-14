using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using ProyectoNice.Models;

namespace ProyectoNice.ViewModels
{
    /// <summary>
    ///     Exporta los planos marcados a PDF (uno por plano o todos en un solo archivo) y a DWG.
    ///     El nombre de cada archivo sale del numero, el nombre y la revision actual del plano.
    /// </summary>
    public sealed class vm_ExportarPlanos : vm_Herramienta
    {
        private const string SeleccionManual = "(seleccion manual)";
        private const string ConfigPredeterminada = "(predeterminada de Revit)";
        private const string VersionDeLaConfig = "(la de la configuracion)";

        //Datos de entrada
        public Document doc;

        //Planos
        private readonly List<m_PlanoExportar> planos;
        public ObservableCollection<m_PlanoExportar> PlanosVisibles { get; } = new ObservableCollection<m_PlanoExportar>();

        private string filtroTB = "";
        public string FiltroTB
        {
            get => filtroTB;
            set
            {
                if (SetProperty(ref filtroTB, value)) Filtrar();
            }
        }

        public List<string> ListaConjuntosCB { get; }
        private readonly List<ViewSheetSet> conjuntos;
        private string conjuntoSeleccCB = SeleccionManual;
        public string ConjuntoSeleccCB
        {
            get => conjuntoSeleccCB;
            set
            {
                if (SetProperty(ref conjuntoSeleccCB, value)) MarcarConjunto();
            }
        }

        public string ResumenSeleccion => $"{planos.Count(p => p.Seleccionado)} de {planos.Count} planos marcados";

        //PDF
        private bool exportarPdfCK = true;
        public bool ExportarPdfCK
        {
            get => exportarPdfCK;
            set => SetProperty(ref exportarPdfCK, value);
        }

        private bool unirPdfCK;
        public bool UnirPdfCK
        {
            get => unirPdfCK;
            set => SetProperty(ref unirPdfCK, value);
        }

        public string NombrePdfUnicoTB { get; set; }
        public List<string> ListaColoresCB { get; } = new List<string> { "Color", "Escala de grises", "Blanco y negro" };
        public string ColorSeleccCB { get; set; } = "Color";

        //DWG
        private bool exportarDwgCK = true;
        public bool ExportarDwgCK
        {
            get => exportarDwgCK;
            set => SetProperty(ref exportarDwgCK, value);
        }

        public List<string> ListaConfigDwgCB { get; }
        public string ConfigDwgSeleccCB { get; set; } = ConfigPredeterminada;
        public List<string> ListaVersionesCB { get; }
        public string VersionSeleccCB { get; set; } = VersionDeLaConfig;
        private readonly Dictionary<string, ACADVersion> versiones;
        public bool UnirVistasCK { get; set; } = true;
        public bool CoordCompartidasCK { get; set; }

        //Nombre y carpeta
        public List<string> ListaFormatosNombreCB { get; } = new List<string> { "Numero - Nombre", "Numero", "Nombre" };
        public string FormatoNombreSeleccCB { get; set; } = "Numero - Nombre";
        public bool IncluirRevisionCK { get; set; }

        private string carpetaTB;
        public string CarpetaTB
        {
            get => carpetaTB;
            set => SetProperty(ref carpetaTB, value);
        }

        //Botones
        public RelayCommand MarcarTodosBT { get; }
        public RelayCommand DesmarcarTodosBT { get; }
        public RelayCommand ElegirCarpetaBT { get; }
        public RelayCommand AbrirCarpetaBT { get; }
        public RelayCommand AceptarBT { get; }

        //Constructor
        public vm_ExportarPlanos(Document doc)
        {
            this.doc = doc;

            planos = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder && !s.IsTemplate)
                .OrderBy(s => s.SheetNumber, new ComparadorNatural())
                .Select(s => new m_PlanoExportar
                {
                    Plano = s,
                    Numero = s.SheetNumber,
                    Nombre = s.Name,
                    Revision = s.get_Parameter(BuiltInParameter.SHEET_CURRENT_REVISION)?.AsString() ?? ""
                })
                .ToList();
            foreach (m_PlanoExportar plano in planos) plano.PropertyChanged += AlCambiarPlano;
            Filtrar();

            conjuntos = new FilteredElementCollector(doc).OfClass(typeof(ViewSheetSet)).Cast<ViewSheetSet>()
                .OrderBy(c => c.Name).ToList();
            ListaConjuntosCB = new List<string> { SeleccionManual };
            ListaConjuntosCB.AddRange(conjuntos.Select(c => c.Name));

            ListaConfigDwgCB = new List<string> { ConfigPredeterminada };
            ListaConfigDwgCB.AddRange(ExportDWGSettings.ListNames(doc).OrderBy(n => n));

            //AutoCAD 2018, 2013... (los valores del enum son R2018, R2013...)
            versiones = Enum.GetValues(typeof(ACADVersion)).Cast<ACADVersion>()
                .Where(v => v.ToString().StartsWith("R"))
                .OrderByDescending(v => v.ToString())
                .ToDictionary(v => "AutoCAD " + v.ToString().Substring(1), v => v);
            ListaVersionesCB = new List<string> { VersionDeLaConfig };
            ListaVersionesCB.AddRange(versiones.Keys);

            NombrePdfUnicoTB = string.IsNullOrWhiteSpace(doc.Title) ? "Planos" : Path.GetFileNameWithoutExtension(doc.Title);
            carpetaTB = CarpetaPredeterminada(doc);

            MarcarTodosBT = new RelayCommand(() => MarcarVisibles(true));
            DesmarcarTodosBT = new RelayCommand(() => MarcarVisibles(false));
            ElegirCarpetaBT = new RelayCommand(ElegirCarpeta);
            AbrirCarpetaBT = new RelayCommand(AbrirCarpeta);
            AceptarBT = new RelayCommand(Validar);
        }

        // ------------------------------------------------------------------
        //  Lista de planos
        // ------------------------------------------------------------------

        private void AlCambiarPlano(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(m_PlanoExportar.Seleccionado)) OnPropertyChanged(nameof(ResumenSeleccion));
        }

        private void Filtrar()
        {
            string filtro = (FiltroTB ?? "").Trim();
            PlanosVisibles.Clear();
            foreach (m_PlanoExportar plano in planos)
            {
                if (filtro.Length == 0
                    || plano.Numero.IndexOf(filtro, StringComparison.OrdinalIgnoreCase) >= 0
                    || plano.Nombre.IndexOf(filtro, StringComparison.OrdinalIgnoreCase) >= 0)
                    PlanosVisibles.Add(plano);
            }
        }

        private void MarcarVisibles(bool valor)
        {
            foreach (m_PlanoExportar plano in PlanosVisibles) plano.Seleccionado = valor;
        }

        //Marca exactamente los planos del conjunto elegido
        private void MarcarConjunto()
        {
            ViewSheetSet conjunto = conjuntos.FirstOrDefault(c => c.Name == ConjuntoSeleccCB);
            if (conjunto == null) return;

            var ids = new HashSet<ElementId>(conjunto.Views.Cast<View>().Select(v => v.Id));
            foreach (m_PlanoExportar plano in planos) plano.Seleccionado = ids.Contains(plano.Plano.Id);
        }

        // ------------------------------------------------------------------
        //  Carpeta de salida
        // ------------------------------------------------------------------

        //Junto al modelo; en Mis documentos si el modelo esta en la nube o sin guardar
        private static string CarpetaPredeterminada(Document doc)
        {
            string ruta = doc.PathName;
            bool esDisco = !string.IsNullOrWhiteSpace(ruta)
                           && ruta.IndexOf("://", StringComparison.Ordinal) < 0
                           && Path.IsPathRooted(ruta);
            if (esDisco) return Path.Combine(Path.GetDirectoryName(ruta), "Planos exportados");

            string modelo = string.IsNullOrWhiteSpace(doc.Title) ? "Modelo" : Sanitizar(Path.GetFileNameWithoutExtension(doc.Title));
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ProyectoNice", modelo, "Planos exportados");
        }

        private void ElegirCarpeta()
        {
            string inicial = Directory.Exists(CarpetaTB) ? CarpetaTB : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
#if NET8_0_OR_GREATER
            var dialogo = new OpenFolderDialog { Title = "Carpeta de salida", InitialDirectory = inicial };
            if (dialogo.ShowDialog() == true) CarpetaTB = dialogo.FolderName;
#else
            //.NET Framework no trae selector de carpetas en WPF: se guarda un archivo ficticio y se toma su carpeta
            var dialogo = new SaveFileDialog
            {
                Title = "Carpeta de salida",
                InitialDirectory = inicial,
                FileName = "Elegir esta carpeta",
                Filter = "Carpeta|*.carpeta",
                OverwritePrompt = false
            };
            if (dialogo.ShowDialog() == true) CarpetaTB = Path.GetDirectoryName(dialogo.FileName);
#endif
        }

        private void AbrirCarpeta()
        {
            if (!Directory.Exists(CarpetaTB))
            {
                Aviso("La carpeta todavia no existe: se crea al exportar.");
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CarpetaTB}\"") { UseShellExecute = true });
        }

        // ------------------------------------------------------------------
        //  Exportacion
        // ------------------------------------------------------------------

        private void Validar()
        {
            List<m_PlanoExportar> marcados = planos.Where(p => p.Seleccionado).ToList();
            if (marcados.Count == 0)
            {
                Aviso("Marque al menos un plano.");
                return;
            }
            if (!ExportarPdfCK && !ExportarDwgCK)
            {
                Aviso("Elija PDF, DWG o los dos.");
                return;
            }
            if (string.IsNullOrWhiteSpace(CarpetaTB) || !Path.IsPathRooted(CarpetaTB))
            {
                Aviso("Escriba una carpeta de salida completa, por ejemplo C:\\Planos.");
                return;
            }
            if (ExportarPdfCK && UnirPdfCK && string.IsNullOrWhiteSpace(Sanitizar(NombrePdfUnicoTB)))
            {
                Aviso("Escriba el nombre del PDF unico.");
                return;
            }

            Ejecutar(() => Exportar(marcados));
        }

        private void Exportar(List<m_PlanoExportar> marcados)
        {
            var errores = new List<string>();
            Dictionary<m_PlanoExportar, string> nombres = NombresDeArchivo(marcados);

            int pdfs = ExportarPdfCK ? ExportarPdf(marcados, nombres, errores) : 0;
            int dwgs = ExportarDwgCK ? ExportarDwg(marcados, nombres, errores) : 0;

            var partes = new List<string>();
            if (ExportarPdfCK) partes.Add($"{pdfs} PDF");
            if (ExportarDwgCK) partes.Add($"{dwgs} DWG");
            string resumen = $"{string.Join(" y ", partes)} en {CarpetaTB}";
            Aviso(errores.Count == 0 ? $"Exportados {resumen}." : $"Exportados {resumen}, con {errores.Count} errores.");

            if (errores.Count > 0)
                TaskDialog.Show("Exportar planos", $"Exportados {resumen}.\n\nNo se pudo exportar:\n" + string.Join("\n", errores.Take(25))
                                                   + (errores.Count > 25 ? $"\n... y {errores.Count - 25} mas." : ""));
        }

        private int ExportarPdf(List<m_PlanoExportar> marcados, Dictionary<m_PlanoExportar, string> nombres, List<string> errores)
        {
            string carpeta = Path.Combine(CarpetaTB, "PDF");
            Directory.CreateDirectory(carpeta);

            //Combine = true con un solo plano es la forma de fijar el nombre del archivo
            if (UnirPdfCK)
            {
                string nombre = Sanitizar(NombrePdfUnicoTB);
                return ExportarUnPdf(carpeta, nombre, marcados.Select(p => p.Plano.Id).ToList(), errores) ? 1 : 0;
            }

            int exportados = 0;
            foreach (m_PlanoExportar plano in marcados)
            {
                if (ExportarUnPdf(carpeta, nombres[plano], new List<ElementId> { plano.Plano.Id }, errores)) exportados++;
            }
            return exportados;
        }

        private bool ExportarUnPdf(string carpeta, string nombre, List<ElementId> ids, List<string> errores)
        {
            if (!BorrarAnterior(Path.Combine(carpeta, nombre + ".pdf"), errores)) return false;

            var opciones = new PDFExportOptions
            {
                Combine = true,
                FileName = nombre,
                ColorDepth = ColorSeleccCB == "Escala de grises" ? ColorDepthType.GrayScale
                    : ColorSeleccCB == "Blanco y negro" ? ColorDepthType.BlackLine
                    : ColorDepthType.Color,
                RasterQuality = RasterQualityType.High,
                PaperFormat = ExportPaperFormat.Default,
                ZoomType = ZoomType.Zoom,
                ZoomPercentage = 100,
                HideCropBoundaries = true,
                HideScopeBoxes = true,
                HideReferencePlane = true,
                HideUnreferencedViewTags = true
            };

            try
            {
                if (doc.Export(carpeta, ids, opciones)) return true;
                errores.Add($"PDF {nombre}: Revit no lo exporto.");
            }
            catch (Exception ex)
            {
                errores.Add($"PDF {nombre}: {ex.Message}");
            }
            return false;
        }

        private int ExportarDwg(List<m_PlanoExportar> marcados, Dictionary<m_PlanoExportar, string> nombres, List<string> errores)
        {
            string carpeta = Path.Combine(CarpetaTB, "DWG");
            Directory.CreateDirectory(carpeta);

            DWGExportOptions opciones = OpcionesDwg();

            int exportados = 0;
            foreach (m_PlanoExportar plano in marcados)
            {
                string nombre = nombres[plano];
                if (!BorrarAnterior(Path.Combine(carpeta, nombre + ".dwg"), errores)) continue;

                try
                {
                    if (doc.Export(carpeta, nombre, new List<ElementId> { plano.Plano.Id }, opciones)) exportados++;
                    else errores.Add($"DWG {nombre}: Revit no lo exporto.");
                }
                catch (Exception ex)
                {
                    errores.Add($"DWG {nombre}: {ex.Message}");
                }
            }
            return exportados;
        }

        //Parte de la configuracion de exportacion elegida y le aplica las opciones de la ventana
        private DWGExportOptions OpcionesDwg()
        {
            DWGExportOptions opciones = null;
            if (ConfigDwgSeleccCB != ConfigPredeterminada)
                opciones = ExportDWGSettings.FindByName(doc, ConfigDwgSeleccCB)?.GetDWGExportOptions();
            opciones ??= new DWGExportOptions();

            opciones.MergedViews = UnirVistasCK;
            opciones.SharedCoords = CoordCompartidasCK;
            if (versiones.TryGetValue(VersionSeleccCB ?? "", out ACADVersion version)) opciones.FileVersion = version;
            return opciones;
        }

        //Revit no siempre reemplaza el archivo: se borra antes. Falla si esta abierto en otro programa
        private static bool BorrarAnterior(string ruta, List<string> errores)
        {
            try
            {
                if (File.Exists(ruta)) File.Delete(ruta);
                return true;
            }
            catch (Exception)
            {
                errores.Add($"{Path.GetFileName(ruta)}: el archivo esta abierto en otro programa.");
                return false;
            }
        }

        // ------------------------------------------------------------------
        //  Nombres de archivo
        // ------------------------------------------------------------------

        private Dictionary<m_PlanoExportar, string> NombresDeArchivo(List<m_PlanoExportar> marcados)
        {
            var nombres = new Dictionary<m_PlanoExportar, string>();
            var usados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (m_PlanoExportar plano in marcados)
            {
                string nombre = FormatoNombreSeleccCB == "Numero" ? plano.Numero
                    : FormatoNombreSeleccCB == "Nombre" ? plano.Nombre
                    : $"{plano.Numero} - {plano.Nombre}";
                if (IncluirRevisionCK && !string.IsNullOrWhiteSpace(plano.Revision)) nombre += $" - Rev {plano.Revision}";

                nombre = Sanitizar(nombre);
                if (nombre.Length == 0) nombre = "Plano";

                //Dos planos con el mismo nombre no se pisan
                string unico = nombre;
                for (int i = 2; !usados.Add(unico); i++) unico = $"{nombre} ({i})";
                nombres[plano] = unico;
            }
            return nombres;
        }

        private static string Sanitizar(string texto)
        {
            if (string.IsNullOrWhiteSpace(texto)) return "";
            char[] invalidos = Path.GetInvalidFileNameChars();
            string limpio = new string(texto.Select(c => invalidos.Contains(c) ? '_' : c).ToArray());
            return limpio.Trim().TrimEnd('.');
        }

        //Ordena A-2 antes que A-10
        private sealed class ComparadorNatural : IComparer<string>
        {
            public int Compare(string x, string y)
            {
                var px = Partes(x);
                var py = Partes(y);
                for (int i = 0; i < Math.Min(px.Count, py.Count); i++)
                {
                    bool nx = long.TryParse(px[i], out long ax);
                    bool ny = long.TryParse(py[i], out long ay);
                    int c = nx && ny ? ax.CompareTo(ay) : string.Compare(px[i], py[i], StringComparison.OrdinalIgnoreCase);
                    if (c != 0) return c;
                }
                return px.Count.CompareTo(py.Count);
            }

            private static List<string> Partes(string s) =>
                System.Text.RegularExpressions.Regex.Split(s ?? "", @"(\d+)").Where(p => p.Length > 0).ToList();
        }
    }
}
