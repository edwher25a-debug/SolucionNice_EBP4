using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ProyectoNice.Utils
{
    /// <summary>
    ///     Lector minimo de .xlsx sin Excel ni librerias: devuelve el texto de cada celda como se ve en Excel en lo que
    ///     importa para los presupuestos (un item 3,10 guardado como numero 3.1 con formato de dos decimales sale "3,10")
    /// </summary>
    public static class LectorExcel
    {
        private static readonly XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace nsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private static readonly XNamespace nsPaq = "http://schemas.openxmlformats.org/package/2006/relationships";

        public static List<string> Hojas(string archivo)
        {
            using ZipArchive zip = Abrir(archivo);
            return LeerXml(zip, "xl/workbook.xml").Descendants(ns + "sheet").Select(h => (string)h.Attribute("name")).ToList();
        }

        //filas[fila][columna], base 0, rellenas con "" hasta la ultima columna usada
        public static List<string[]> LeerHoja(string archivo, string hoja)
        {
            using ZipArchive zip = Abrir(archivo);
            List<string> compartidas = TextosCompartidos(zip);
            List<int> decimales = DecimalesPorEstilo(zip);

            XElement libro = LeerXml(zip, "xl/workbook.xml");
            string rid = (string)libro.Descendants(ns + "sheet").First(h => (string)h.Attribute("name") == hoja).Attribute(nsRel + "id");
            string destino = (string)LeerXml(zip, "xl/_rels/workbook.xml.rels").Elements(nsPaq + "Relationship")
                .First(r => (string)r.Attribute("Id") == rid).Attribute("Target");
            string ruta = destino.StartsWith("/") ? destino.TrimStart('/') : "xl/" + destino;

            var filas = new SortedDictionary<int, Dictionary<int, string>>();
            int maxColumna = 0;
            foreach (XElement celda in LeerXml(zip, ruta).Descendants(ns + "c"))
            {
                (int fila, int columna) = Posicion((string)celda.Attribute("r"));
                string texto = Texto(celda, compartidas, decimales);
                if (texto.Length == 0) continue;
                if (!filas.TryGetValue(fila, out var celdas)) filas[fila] = celdas = new Dictionary<int, string>();
                celdas[columna] = texto;
                maxColumna = Math.Max(maxColumna, columna);
            }

            var resultado = new List<string[]>();
            int ultima = filas.Count == 0 ? -1 : filas.Keys.Last();
            for (int f = 0; f <= ultima; f++)
            {
                string[] linea = Enumerable.Repeat("", maxColumna + 1).ToArray();
                if (filas.TryGetValue(f, out var celdas))
                    foreach (var c in celdas) linea[c.Key] = c.Value;
                resultado.Add(linea);
            }

            return resultado;
        }

        private static string Texto(XElement celda, List<string> compartidas, List<int> decimales)
        {
            string tipo = (string)celda.Attribute("t") ?? "n";
            if (tipo == "inlineStr") return string.Concat(celda.Descendants(ns + "t").Select(t => t.Value)).Trim();

            string valor = (string)celda.Element(ns + "v") ?? "";
            if (valor.Length == 0) return "";
            if (tipo == "s") return compartidas[int.Parse(valor, CultureInfo.InvariantCulture)].Trim();
            if (tipo != "n") return valor.Trim();

            //Numero: con los decimales del formato de la celda y coma decimal (como se escriben los items)
            if (!double.TryParse(valor, NumberStyles.Float, CultureInfo.InvariantCulture, out double numero)) return valor;
            int estilo = (int?)celda.Attribute("s") ?? 0;
            int d = estilo < decimales.Count ? decimales[estilo] : -1;
            string texto = d >= 0 ? numero.ToString("F" + d, CultureInfo.InvariantCulture) : numero.ToString("R", CultureInfo.InvariantCulture);
            return texto.Replace('.', ',');
        }

        private static List<string> TextosCompartidos(ZipArchive zip)
        {
            if (zip.GetEntry("xl/sharedStrings.xml") == null) return new List<string>();
            return LeerXml(zip, "xl/sharedStrings.xml").Elements(ns + "si")
                .Select(si => string.Concat(si.Descendants(ns + "t").Select(t => t.Value))).ToList();
        }

        //Decimales fijos de cada estilo de celda (-1 = formato general)
        private static List<int> DecimalesPorEstilo(ZipArchive zip)
        {
            if (zip.GetEntry("xl/styles.xml") == null) return new List<int>();
            XElement estilos = LeerXml(zip, "xl/styles.xml");
            var formatos = estilos.Descendants(ns + "numFmt")
                .ToDictionary(f => (int)f.Attribute("numFmtId"), f => (string)f.Attribute("formatCode"));
            var incorporados = new Dictionary<int, string> { [1] = "0", [2] = "0.00", [3] = "#,##0", [4] = "#,##0.00" };

            return (estilos.Element(ns + "cellXfs")?.Elements(ns + "xf") ?? Enumerable.Empty<XElement>()).Select(xf =>
            {
                int id = (int?)xf.Attribute("numFmtId") ?? 0;
                if (!formatos.TryGetValue(id, out string codigo) && !incorporados.TryGetValue(id, out codigo)) return -1;
                Match m = Regex.Match(codigo.Split(';')[0], @"\.(0+)");
                return m.Success ? m.Groups[1].Length : 0;
            }).ToList();
        }

        private static (int fila, int columna) Posicion(string referencia)
        {
            int columna = 0, i = 0;
            for (; i < referencia.Length && char.IsLetter(referencia[i]); i++) columna = columna * 26 + (char.ToUpperInvariant(referencia[i]) - 'A' + 1);
            return (int.Parse(referencia.Substring(i), CultureInfo.InvariantCulture) - 1, columna - 1);
        }

        //Lectura compartida: funciona aunque el archivo este abierto en Excel
        private static ZipArchive Abrir(string archivo) =>
            new ZipArchive(new FileStream(archivo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), ZipArchiveMode.Read);

        private static XElement LeerXml(ZipArchive zip, string ruta)
        {
            using Stream flujo = zip.GetEntry(ruta)!.Open();
            return XElement.Load(flujo);
        }
    }
}
