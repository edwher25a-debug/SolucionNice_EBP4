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
    [Transaction(TransactionMode.ReadOnly)]
    public class ExportarFichasCmd : ExternalCommand
    {
        /// <summary>Tolerancia vertical para considerar que un elemento se apoya en otro (metros).</summary>
        private const double ToleranciaApoyoM = 0.35;

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
            BuiltInCategory.OST_Railing,
            BuiltInCategory.OST_GenericModel,
            BuiltInCategory.OST_StructuralStiffener
        };

        public override void Execute()
        {
            var doc = Document;

            var carpeta = ResolverCarpetaSalida(doc);
            Directory.CreateDirectory(carpeta);

            var elementos = RecolectarElementos(doc);
            if (elementos.Count == 0)
            {
                TaskDialog.Show("Exportar fichas",
                    "No se encontraron elementos en las categorias exportadas.\n" +
                    "Revisa CategoriasExportadas en ExportarFichasCmd.cs.");
                return;
            }

            var fichas = AgruparEnFichas(doc, elementos);
            CalcularApoyos(fichas);

            foreach (var ficha in fichas)
            {
                var ruta = Path.Combine(carpeta, Sanitizar(ficha.Nombre) + ".md");
                File.WriteAllText(ruta, RenderizarFicha(ficha, doc), new UTF8Encoding(false));
            }

            File.WriteAllText(Path.Combine(carpeta, "00-indice.md"),
                RenderizarIndice(fichas, doc), new UTF8Encoding(false));

            TaskDialog.Show("Exportar fichas",
                $"{fichas.Count} fichas escritas a partir de {elementos.Count} elementos.\n\n{carpeta}");
        }

        // ------------------------------------------------------------------
        //  Recoleccion
        // ------------------------------------------------------------------

        private static List<Element> RecolectarElementos(Document doc)
        {
            var filtro = new ElementMulticategoryFilter(CategoriasExportadas);
            return new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .WherePasses(filtro)
                .Where(e => e.Category != null)
                .ToList();
        }

        // ------------------------------------------------------------------
        //  Agrupacion: una ficha por elemento coordinable, no por instancia
        // ------------------------------------------------------------------

        private static List<Ficha> AgruparEnFichas(Document doc, List<Element> elementos)
        {
            var mapa = new Dictionary<string, Ficha>(StringComparer.OrdinalIgnoreCase);

            foreach (var elem in elementos)
            {
                var clave = ConstruirClave(doc, elem);

                if (!mapa.TryGetValue(clave, out var ficha))
                {
                    ficha = new Ficha
                    {
                        Nombre = clave,
                        Categoria = elem.Category.Name,
                        Tipo = NombreDeTipo(doc, elem),
                        Nivel = NombreDeNivel(doc, elem),
                        Agrupador = LeerAgrupador(elem)
                    };
                    mapa[clave] = ficha;
                }

                ficha.Instancias.Add(elem);
                ficha.VolumenM3 += VolumenM3(elem);
                ficha.ExpandirCaja(elem);

                var material = NombreDeMaterial(doc, elem);
                if (!string.IsNullOrWhiteSpace(material) && !ficha.Materiales.Contains(material))
                    ficha.Materiales.Add(material);
            }

            return mapa.Values.OrderBy(f => f.Categoria).ThenBy(f => f.Nombre).ToList();
        }

        /// <summary>
        ///     Clave de agrupacion. Si el modelo tiene un parametro de abscisa o eje se usa ese,
        ///     que es lo que hace que la ficha represente "Pila P-3" y no "todas las columnas".
        ///     Si no existe, cae a categoria + tipo + nivel.
        /// </summary>
        private static string ConstruirClave(Document doc, Element elem)
        {
            var agrupador = LeerAgrupador(elem);
            if (!string.IsNullOrWhiteSpace(agrupador))
                return $"{elem.Category.Name} - {agrupador}";

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
        ///     Deduce "apoya_en" comparando cajas envolventes agregadas por ficha:
        ///     la base de A coincide en cota con el tope de B y sus proyecciones en planta se solapan.
        ///     Es una inferencia, no un hecho del modelo; se marca como tal en la ficha.
        /// </summary>
        private static void CalcularApoyos(List<Ficha> fichas)
        {
            var conCaja = fichas.Where(f => f.TieneCaja).ToList();

            foreach (var arriba in conCaja)
            {
                foreach (var abajo in conCaja)
                {
                    if (ReferenceEquals(arriba, abajo)) continue;

                    var salto = Math.Abs(arriba.MinZ - abajo.MaxZ);
                    if (salto > ToleranciaApoyoM) continue;
                    if (!SeSolapanEnPlanta(arriba, abajo)) continue;

                    arriba.ApoyaEn.Add(abajo.Nombre);
                    abajo.Soporta.Add(arriba.Nombre);
                }
            }
        }

        private static bool SeSolapanEnPlanta(Ficha a, Ficha b)
        {
            return a.MinX <= b.MaxX && b.MinX <= a.MaxX
                && a.MinY <= b.MaxY && b.MinY <= a.MaxY;
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
                foreach (var n in f.ApoyaEn.OrderBy(x => x)) sb.AppendLine($"- apoya_en -> {n}  <!-- INFERRED: geometria -->");
                foreach (var n in f.Soporta.OrderBy(x => x)) sb.AppendLine($"- soporta -> {n}  <!-- INFERRED: geometria -->");
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
            sb.AppendLine($"Exportado el {DateTime.Now:yyyy-MM-dd HH:mm} desde {Path.GetFileName(doc.PathName)}");
            sb.AppendLine();
            sb.AppendLine($"{fichas.Count} fichas · {fichas.Sum(f => f.Instancias.Count)} instancias");
            sb.AppendLine();

            foreach (var grupo in fichas.GroupBy(f => f.Categoria).OrderBy(g => g.Key))
            {
                sb.AppendLine($"## {grupo.Key}");
                sb.AppendLine();
                foreach (var f in grupo.OrderBy(f => f.Nombre))
                    sb.AppendLine($"- [{f.Nombre}]({Sanitizar(f.Nombre)}.md) — {f.Instancias.Count} instancia(s)");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        // ------------------------------------------------------------------
        //  Ayudas
        // ------------------------------------------------------------------

        private static string ResolverCarpetaSalida(Document doc)
        {
            var baseDir = string.IsNullOrWhiteSpace(doc.PathName)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : Path.GetDirectoryName(doc.PathName);

            return Path.Combine(baseDir ?? ".", "fichas");
        }

        private static string NombreDeProyecto(Document doc)
        {
            var info = doc.ProjectInformation;
            if (info != null && !string.IsNullOrWhiteSpace(info.Name)) return info.Name;
            return string.IsNullOrWhiteSpace(doc.Title) ? "Modelo sin titulo" : doc.Title;
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

        private static double VolumenM3(Element elem)
        {
            var p = elem.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
            if (p != null && p.HasValue) return MetodoUnidades.Pies3AMetros3(p.AsDouble());

            try
            {
                var solidos = MetodoGeometria.ObtenerSolidos(elem);
                return MetodoUnidades.Pies3AMetros3(solidos.Sum(s => s.Volume));
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

        private static string Sanitizar(string nombre)
        {
            var sb = new StringBuilder(nombre.Length);
            foreach (var c in nombre)
                sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);
            return sb.ToString().Trim();
        }

        // ------------------------------------------------------------------

        private class Ficha
        {
            public string Nombre;
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

                var min = caja.Min;
                var max = caja.Max;

                var nMinX = MetodoUnidades.PiesAMetros(min.X);
                var nMinY = MetodoUnidades.PiesAMetros(min.Y);
                var nMinZ = MetodoUnidades.PiesAMetros(min.Z);
                var nMaxX = MetodoUnidades.PiesAMetros(max.X);
                var nMaxY = MetodoUnidades.PiesAMetros(max.Y);
                var nMaxZ = MetodoUnidades.PiesAMetros(max.Z);

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
