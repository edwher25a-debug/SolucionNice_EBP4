using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Nice3point.Revit.Toolkit.External;
using ProyectoNice.Utils;

namespace ProyectoNice.Commands
{
    /// <summary>
    ///     Exporta el modelo a fichas markdown, una por elemento coordinable.
    ///     La salida esta pensada para alimentar un grafo de conocimiento:
    ///     jerarquia, relaciones y decisiones, no un volcado de parametros.
    /// </summary>
    [UsedImplicitly]
    [Transaction(TransactionMode.Manual)]
    public class ExportarFichasCmd : ExternalCommand
    {
        /// <summary>Tolerancia vertical para considerar que un elemento se apoya en otro (metros).</summary>
        private const double ToleranciaApoyoM = 0.35;

        /// <summary>
        ///     Fraccion minima de elementos de una categoria que deben tener el parametro
        ///     agrupador para que se use en esa categoria. Evita que 3 elementos con
        ///     "Marca de tipo" rellena creen fichas fantasma junto a las agrupadas por tipo.
        /// </summary>
        private const double CoberturaMinimaAgrupador = 0.60;

        /// <summary>Por encima de este numero de elementos no se calcula volumen con geometria (lento).</summary>
        private const int LimiteVolumenPorGeometria = 2000;

        /// <summary>
        ///     Si el modelo usa categorias que no estan en la lista de abajo —por ejemplo las
        ///     categorias de puente de Revit 2023+— pon esto en true y se recorren todas
        ///     las categorias de modelo.
        /// </summary>
        private const bool UsarTodasLasCategoriasDeModelo = false;

        /// <summary>Parametros que se prueban, en orden, para agrupar instancias en una ficha.</summary>
        private static readonly string[] ParametrosAgrupador =
        {
            "Abscisa", "Eje", "Elemento", "Marca de tipo", "Mark", "Type Mark"
        };

        /// <summary>Categorias que se consideran coordinables. Ampliar segun el modelo.</summary>
        private static readonly BuiltInCategory[] CategoriasExportadas =
        {
            BuiltInCategory.OST_StructuralFoundation,
            BuiltInCategory.OST_StructuralColumns,
            BuiltInCategory.OST_StructuralFraming,
            BuiltInCategory.OST_Floors,
            BuiltInCategory.OST_Walls,
            BuiltInCategory.OST_Stairs,
            BuiltInCategory.OST_StairsRailing,
            BuiltInCategory.OST_GenericModel,
            BuiltInCategory.OST_StructuralStiffener
        };

        public override void Execute() => Exportar(Document);

        public static void Exportar(Document doc)
        {
            try
            {
                if (doc == null)
                {
                    TaskDialog.Show("Exportar fichas", "No hay ningun modelo abierto.");
                    return;
                }

                string carpeta;
                if (!TryResolverCarpetaSalida(doc, out carpeta, out var motivo))
                {
                    TaskDialog.Show("Exportar fichas", motivo);
                    return;
                }

                Directory.CreateDirectory(carpeta);

                var elementos = RecolectarElementos(doc);
                if (elementos.Count == 0)
                {
                    TaskDialog.Show("Exportar fichas",
                        "No se encontraron elementos en las categorias exportadas.\n" +
                        "Prueba poniendo UsarTodasLasCategoriasDeModelo en true.");
                    return;
                }

                var usarGeometria = elementos.Count <= LimiteVolumenPorGeometria;
                List<Caja> cajas;
                var fichas = AgruparEnFichas(doc, elementos, usarGeometria, out cajas);
                CalcularApoyos(cajas);

                AsignarNombresDeArchivo(fichas);

                foreach (var ficha in fichas)
                    File.WriteAllText(Path.Combine(carpeta, ficha.Archivo),
                        RenderizarFicha(ficha, doc), new UTF8Encoding(false));

                File.WriteAllText(Path.Combine(carpeta, "00-indice.md"),
                    RenderizarIndice(fichas, doc), new UTF8Encoding(false));

                var aviso = usarGeometria
                    ? string.Empty
                    : $"\n\nNota: {elementos.Count} elementos superan el limite de {LimiteVolumenPorGeometria};" +
                      " el volumen se leyo solo del parametro, sin calcular geometria.";

                TaskDialog.Show("Exportar fichas",
                    $"{fichas.Count} fichas escritas a partir de {elementos.Count} elementos.\n\n{carpeta}{aviso}");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Exportar fichas - ERROR", ex.ToString());
            }
        }

        // ------------------------------------------------------------------
        //  Recoleccion
        // ------------------------------------------------------------------

        private static List<Element> RecolectarElementos(Document doc)
        {
            if (UsarTodasLasCategoriasDeModelo)
            {
                return new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Where(e => e.Category != null
                                && e.Category.CategoryType == CategoryType.Model
                                && e.get_BoundingBox(null) != null)
                    .ToList();
            }

            var filtro = new ElementMulticategoryFilter(CategoriasExportadas);
            return new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(filtro)
                .Where(e => e.Category != null)
                .ToList();
        }

        // ------------------------------------------------------------------
        //  Agrupacion
        // ------------------------------------------------------------------

        private static List<Ficha> AgruparEnFichas(Document doc, List<Element> elementos, bool usarGeometria,
            out List<Caja> cajas)
        {
            cajas = new List<Caja>();
            // Decision por categoria: el parametro agrupador solo se usa si lo tiene
            // la mayoria de la categoria. Si no, toda la categoria cae a tipo + nivel,
            // y el resultado queda consistente en vez de mezclado.
            var categoriasConAgrupador = elementos
                .GroupBy(e => e.Category.Name)
                .Where(g => g.Count(e => !string.IsNullOrWhiteSpace(LeerAgrupador(e)))
                            >= g.Count() * CoberturaMinimaAgrupador)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var mapa = new Dictionary<string, Ficha>(StringComparer.OrdinalIgnoreCase);

            foreach (var elem in elementos)
            {
                var usaAgrupador = categoriasConAgrupador.Contains(elem.Category.Name);
                var clave = ConstruirClave(doc, elem, usaAgrupador);

                if (!mapa.TryGetValue(clave, out var ficha))
                {
                    ficha = new Ficha
                    {
                        Nombre = clave,
                        Categoria = elem.Category.Name,
                        Tipo = NombreDeTipo(doc, elem),
                        Nivel = NombreDeNivel(doc, elem),
                        Agrupador = usaAgrupador ? LeerAgrupador(elem) : null
                    };
                    mapa[clave] = ficha;
                }

                ficha.Instancias.Add(elem);
                ficha.VolumenM3 += VolumenM3(elem, usarGeometria);
                ficha.ExpandirCaja(elem);

                var caja = Caja.Desde(elem, ficha);
                if (caja != null) cajas.Add(caja);

                var material = NombreDeMaterial(doc, elem);
                if (!string.IsNullOrWhiteSpace(material) && !ficha.Materiales.Contains(material))
                    ficha.Materiales.Add(material);
            }

            return mapa.Values.OrderBy(f => f.Categoria).ThenBy(f => f.Nombre).ToList();
        }

        private static string ConstruirClave(Document doc, Element elem, bool usaAgrupador)
        {
            if (usaAgrupador)
            {
                var agrupador = LeerAgrupador(elem);
                if (!string.IsNullOrWhiteSpace(agrupador))
                    return $"{elem.Category.Name} - {agrupador}";
            }

            var nivel = NombreDeNivel(doc, elem);
            var tipo = NombreDeTipo(doc, elem);
            return string.IsNullOrWhiteSpace(nivel)
                ? $"{elem.Category.Name} - {tipo}"
                : $"{elem.Category.Name} - {tipo} - {nivel}";
        }

        private static string LeerAgrupador(Element elem)
        {
            foreach (var nombre in ParametrosAgrupador)
            {
                var p = elem.LookupParameter(nombre);
                if (p == null || !p.HasValue) continue;

                var valor = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
                if (!string.IsNullOrWhiteSpace(valor)) return valor.Trim();
            }
            return null;
        }

        // ------------------------------------------------------------------
        //  Relaciones de apoyo (INFERIDAS por geometria)
        // ------------------------------------------------------------------

        /// <summary>
        ///     Deduce "apoya_en" comparando INSTANCIA contra INSTANCIA, no ficha contra ficha.
        ///     La caja agregada de una ficha cubre todo el edificio, asi que comparando a ese
        ///     nivel todo toca con todo: 11 columnas salian apoyadas en 13 zapatas distintas.
        ///     Cada instancia busca solo lo que tiene justo debajo, y la ficha hereda los
        ///     tipos que sus instancias tocan de verdad.
        ///
        ///     Para no degradar a O(n^2) en modelos grandes, las cajas se ordenan por cota
        ///     superior y cada elemento se compara solo contra su banda vertical, localizada
        ///     con busqueda binaria.
        /// </summary>
        private static void CalcularApoyos(List<Caja> cajas)
        {
            if (cajas.Count == 0) return;

            var porTope = cajas.OrderBy(c => c.MaxZ).ToList();
            var topes = new double[porTope.Count];
            for (var i = 0; i < porTope.Count; i++) topes[i] = porTope[i].MaxZ;

            foreach (var arriba in cajas)
            {
                var inicio = PrimerIndiceDesde(topes, arriba.MinZ - ToleranciaApoyoM);
                var limite = arriba.MinZ + ToleranciaApoyoM;

                for (var i = inicio; i < porTope.Count; i++)
                {
                    var abajo = porTope[i];
                    if (abajo.MaxZ > limite) break;

                    if (ReferenceEquals(abajo, arriba)) continue;
                    if (ReferenceEquals(abajo.Ficha, arriba.Ficha)) continue;
                    if (!SeSolapanEnPlanta(arriba, abajo)) continue;

                    arriba.Ficha.ApoyaEn.Add(abajo.Ficha.Nombre);
                    abajo.Ficha.Soporta.Add(arriba.Ficha.Nombre);
                }
            }
        }

        /// <summary>Primer indice cuyo valor es &gt;= objetivo, sobre un array ya ordenado.</summary>
        private static int PrimerIndiceDesde(double[] ordenados, double objetivo)
        {
            var lo = 0;
            var hi = ordenados.Length;
            while (lo < hi)
            {
                var medio = lo + (hi - lo) / 2;
                if (ordenados[medio] < objetivo) lo = medio + 1;
                else hi = medio;
            }
            return lo;
        }

        private static bool SeSolapanEnPlanta(Caja a, Caja b)
        {
            return a.MinX <= b.MaxX && b.MinX <= a.MaxX
                && a.MinY <= b.MaxY && b.MinY <= a.MaxY;
        }

        // ------------------------------------------------------------------
        //  Nombres de archivo: seguros para markdown y sin colisiones
        // ------------------------------------------------------------------

        private static void AsignarNombresDeArchivo(List<Ficha> fichas)
        {
            var usados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var ficha in fichas)
            {
                var basePropuesta = Slug(ficha.Nombre);
                if (string.IsNullOrWhiteSpace(basePropuesta)) basePropuesta = "ficha";

                var candidata = basePropuesta;
                var n = 2;
                while (!usados.Add(candidata + ".md"))
                    candidata = basePropuesta + "-" + n++;

                ficha.Archivo = candidata + ".md";
            }
        }

        /// <summary>
        ///     Convierte un nombre a un identificador seguro: minusculas, sin espacios,
        ///     sin parentesis ni acentos. Los parentesis rompen los enlaces markdown y
        ///     los espacios obligan a codificar la URL, asi que se eliminan de raiz.
        /// </summary>
        private static string Slug(string texto)
        {
            var normalizado = texto.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalizado.Length);
            var guionPendiente = false;

            foreach (var c in normalizado)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;

                if (char.IsLetterOrDigit(c))
                {
                    if (guionPendiente && sb.Length > 0) sb.Append('-');
                    guionPendiente = false;
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                {
                    guionPendiente = true;
                }
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Render
        // ------------------------------------------------------------------

        private static string RenderizarFicha(Ficha f, Document doc)
        {
            var sb = new StringBuilder();

            sb.AppendLine($"# {f.Nombre}");
            sb.AppendLine();
            sb.AppendLine($"**Proyecto:** {NombreDeProyecto(doc)}");
            sb.AppendLine($"**Categoria:** {f.Categoria}");
            sb.AppendLine($"**Tipo:** {f.Tipo}");
            if (!string.IsNullOrWhiteSpace(f.Nivel)) sb.AppendLine($"**Nivel:** {f.Nivel}");
            if (!string.IsNullOrWhiteSpace(f.Agrupador)) sb.AppendLine($"**Agrupador:** {f.Agrupador}");
            sb.AppendLine($"**Instancias:** {f.Instancias.Count}");
            sb.AppendLine();

            sb.AppendLine("## Composicion");
            if (f.TieneCaja)
            {
                sb.AppendLine($"- Envolvente: {N(f.MaxX - f.MinX)} x {N(f.MaxY - f.MinY)} x {N(f.MaxZ - f.MinZ)} m");
                sb.AppendLine($"- Cota base: {N(f.MinZ)} m · Cota superior: {N(f.MaxZ)} m");
            }
            if (f.VolumenM3 > 0) sb.AppendLine($"- Volumen total: {N(f.VolumenM3)} m3");
            if (f.Materiales.Count > 0) sb.AppendLine($"- Materiales: {string.Join(", ", f.Materiales)}");
            sb.AppendLine($"- IDs Revit: {string.Join(", ", f.Instancias.Take(20).Select(e => IdTexto(e.Id)))}"
                          + (f.Instancias.Count > 20 ? $" (+{f.Instancias.Count - 20} mas)" : string.Empty));
            sb.AppendLine();

            sb.AppendLine("## Relaciones");
            if (f.ApoyaEn.Count == 0 && f.Soporta.Count == 0)
            {
                sb.AppendLine("- _Sin relaciones geometricas detectadas._");
            }
            else
            {
                foreach (var n in f.ApoyaEn.OrderBy(x => x))
                    sb.AppendLine($"- apoya_en -> {n}  <!-- INFERRED: geometria -->");
                foreach (var n in f.Soporta.OrderBy(x => x))
                    sb.AppendLine($"- soporta -> {n}  <!-- INFERRED: geometria -->");
            }
            sb.AppendLine();

            sb.AppendLine("## Decisiones");
            sb.AppendLine("<!-- Esta seccion la escribe una persona. Es la unica parte que se reutiliza");
            sb.AppendLine("     en el proximo proyecto, y la unica que el modelo no puede exportar.");
            sb.AppendLine("     Cita siempre la fuente: estudio, norma, acta. -->");
            sb.AppendLine();
            sb.AppendLine("- **[Decision pendiente]** Por que esta resuelto asi.");
            sb.AppendLine("  Fuente: ");
            sb.AppendLine();

            return sb.ToString();
        }

        private static string RenderizarIndice(List<Ficha> fichas, Document doc)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {NombreDeProyecto(doc)} - Indice de fichas");
            sb.AppendLine();
            sb.AppendLine($"Exportado el {DateTime.Now:yyyy-MM-dd HH:mm} desde {NombreDeArchivoModelo(doc)}");
            sb.AppendLine();
            sb.AppendLine($"{fichas.Count} fichas · {fichas.Sum(f => f.Instancias.Count)} instancias");
            sb.AppendLine();

            foreach (var grupo in fichas.GroupBy(f => f.Categoria).OrderBy(g => g.Key))
            {
                sb.AppendLine($"## {grupo.Key}");
                sb.AppendLine();
                foreach (var f in grupo.OrderBy(x => x.Nombre))
                    sb.AppendLine($"- [{f.Nombre}]({f.Archivo}) — {f.Instancias.Count} instancia(s)");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Ayudas
        // ------------------------------------------------------------------

        /// <summary>
        ///     Resuelve donde escribir. Un central en servidor (RSN://) o en la nube no
        ///     tiene ruta de disco, asi que en ese caso se cae a Mis Documentos en vez
        ///     de reventar dentro de Path.GetDirectoryName.
        /// </summary>
        private static bool TryResolverCarpetaSalida(Document doc, out string carpeta, out string motivo)
        {
            carpeta = null;
            motivo = null;

            var misDocumentos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var nombre = NombreDeArchivoModelo(doc);

            try
            {
                var ruta = doc.PathName;

                var esDisco = !string.IsNullOrWhiteSpace(ruta)
                              && ruta.IndexOf("://", StringComparison.Ordinal) < 0
                              && Path.IsPathRooted(ruta);

                var baseDir = esDisco ? Path.GetDirectoryName(ruta) : null;

                if (string.IsNullOrWhiteSpace(baseDir))
                    baseDir = Path.Combine(misDocumentos, "ProyectoNice", Slug(nombre));

                carpeta = Path.Combine(baseDir, "fichas");
                return true;
            }
            catch (Exception ex)
            {
                motivo = "No se pudo resolver la carpeta de salida.\n\n" + ex.Message;
                return false;
            }
        }

        private static string NombreDeArchivoModelo(Document doc)
        {
            var ruta = doc.PathName;
            if (!string.IsNullOrWhiteSpace(ruta))
            {
                try { return Path.GetFileName(ruta); } catch { /* ruta no-disco */ }
            }
            return string.IsNullOrWhiteSpace(doc.Title) ? "modelo" : doc.Title;
        }

        private static string NombreDeProyecto(Document doc)
        {
            var info = doc.ProjectInformation;
            var nombre = info?.Name;

            // "Project Name" es el valor por defecto de la plantilla de Revit:
            // no identifica nada, asi que se prefiere el nombre del archivo.
            if (string.IsNullOrWhiteSpace(nombre)
                || nombre.Equals("Project Name", StringComparison.OrdinalIgnoreCase)
                || nombre.Equals("Nombre del proyecto", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileNameWithoutExtension(NombreDeArchivoModelo(doc));
            }

            return nombre;
        }

        private static string NombreDeTipo(Document doc, Element elem)
        {
            var tipo = doc.GetElement(elem.GetTypeId()) as ElementType;
            return tipo?.Name ?? elem.Name ?? "(sin tipo)";
        }

        private static string NombreDeNivel(Document doc, Element elem)
        {
            if (elem.LevelId == null || elem.LevelId == ElementId.InvalidElementId) return null;
            return (doc.GetElement(elem.LevelId) as Level)?.Name;
        }

        private static string NombreDeMaterial(Document doc, Element elem)
        {
            try
            {
                var ids = elem.GetMaterialIds(false);
                var primero = ids?.FirstOrDefault();
                if (primero == null || primero == ElementId.InvalidElementId) return null;
                return (doc.GetElement(primero) as Material)?.Name;
            }
            catch
            {
                return null;
            }
        }

        private static double VolumenM3(Element elem, bool usarGeometria)
        {
            var p = elem.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
            if (p != null && p.HasValue) return MetodoUnidades.Pies3AMetros3(p.AsDouble());

            if (!usarGeometria) return 0d;

            try
            {
                var solidos = MetodoGeometria.ObtenerSolidos(elem);
                return solidos == null ? 0d : MetodoUnidades.Pies3AMetros3(solidos.Sum(s => s.Volume));
            }
            catch
            {
                return 0d;
            }
        }

        /// <summary>ElementId.IntegerValue quedo obsoleto en Revit 2024; alli se usa Value (long).</summary>
        private static string IdTexto(ElementId id)
        {
#if REVIT2024_OR_GREATER
            return id.Value.ToString(CultureInfo.InvariantCulture);
#else
            return id.IntegerValue.ToString(CultureInfo.InvariantCulture);
#endif
        }

        private static string N(double valor)
        {
            return valor.ToString("0.00", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------

        /// <summary>Caja envolvente de UNA instancia, en metros, con la ficha a la que pertenece.</summary>
        private class Caja
        {
            public Ficha Ficha;
            public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

            public static Caja Desde(Element elem, Ficha ficha)
            {
                var bb = elem.get_BoundingBox(null);
                if (bb == null) return null;

                return new Caja
                {
                    Ficha = ficha,
                    MinX = MetodoUnidades.PiesAMetros(bb.Min.X),
                    MinY = MetodoUnidades.PiesAMetros(bb.Min.Y),
                    MinZ = MetodoUnidades.PiesAMetros(bb.Min.Z),
                    MaxX = MetodoUnidades.PiesAMetros(bb.Max.X),
                    MaxY = MetodoUnidades.PiesAMetros(bb.Max.Y),
                    MaxZ = MetodoUnidades.PiesAMetros(bb.Max.Z)
                };
            }
        }

        private class Ficha
        {
            public string Nombre;
            public string Archivo;
            public string Categoria;
            public string Tipo;
            public string Nivel;
            public string Agrupador;
            public double VolumenM3;

            public readonly List<Element> Instancias = new List<Element>();
            public readonly List<string> Materiales = new List<string>();
            public readonly SortedSet<string> ApoyaEn = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly SortedSet<string> Soporta = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            public bool TieneCaja;
            public double MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

            public void ExpandirCaja(Element elem)
            {
                var caja = elem.get_BoundingBox(null);
                if (caja == null) return;

                var nMinX = MetodoUnidades.PiesAMetros(caja.Min.X);
                var nMinY = MetodoUnidades.PiesAMetros(caja.Min.Y);
                var nMinZ = MetodoUnidades.PiesAMetros(caja.Min.Z);
                var nMaxX = MetodoUnidades.PiesAMetros(caja.Max.X);
                var nMaxY = MetodoUnidades.PiesAMetros(caja.Max.Y);
                var nMaxZ = MetodoUnidades.PiesAMetros(caja.Max.Z);

                if (!TieneCaja)
                {
                    MinX = nMinX; MinY = nMinY; MinZ = nMinZ;
                    MaxX = nMaxX; MaxY = nMaxY; MaxZ = nMaxZ;
                    TieneCaja = true;
                    return;
                }

                MinX = Math.Min(MinX, nMinX);
                MinY = Math.Min(MinY, nMinY);
                MinZ = Math.Min(MinZ, nMinZ);
                MaxX = Math.Max(MaxX, nMaxX);
                MaxY = Math.Max(MaxY, nMaxY);
                MaxZ = Math.Max(MaxZ, nMaxZ);
            }
        }
    }
}
